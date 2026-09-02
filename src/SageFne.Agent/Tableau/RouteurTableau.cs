using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Validation;
using FneOptions = SageFne.Core.Configuration.FneOptions;

namespace SageFne.Agent.Tableau;

/// <summary>
/// Le tableau de bord : la liste des factures, et le bouton qui en certifie une.
/// </summary>
/// <remarks>
/// <b>Il ne juge rien par lui-même.</b> La liste vient de
/// <see cref="MoteurSurveillance"/>, exactement celui du service, avec le même
/// vérificateur de stabilité et le même suivi des refus — deux singletons
/// partagés. Un tableau qui referait ses propres calculs afficherait tôt ou tard
/// autre chose que ce que l'agent fait réellement, et c'est l'écran qu'on
/// croirait.
///
/// Le bouton, lui, <b>passe outre la stabilité et le mode</b>, et c'est tout son
/// objet : un humain qui clique déclare que la saisie est finie. Il ne passe
/// jamais outre les contrôles métier ni le registre — <see cref="InvoiceSender"/>
/// refuse toute pièce qui n'est pas « à certifier », et inscrit
/// <c>Sending</c> avant l'appel.
///
/// Aucun socket ici : le routage rend un <see cref="ReponseHttp"/>. C'est ce qui
/// le rend éprouvable, bouton compris, sans ouvrir de port.
/// </remarks>
public sealed class RouteurTableau(
    IServiceProvider fabrique,
    IOptions<AgentOptions> reglages,
    VerificateurStabilite stabilite,
    SuiviRefus refus,
    FneApiOptions api,
    ISondeReseau sonde,
    ILogger<RouteurTableau> logger)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AgentOptions _reglages = reglages.Value;

    // Les envois en cours, par pièce. Un double-clic sur « Certifier » lance
    // deux appels concurrents : le premier inscrit Sending, mais le second a
    // déjà lu le registre avant cette inscription et part quand même. Deux POST,
    // deux factures chez la DGI, et rien pour les défaire. C'est arrivé une fois
    // par une autre voie — la pièce 1072 — et il a fallu un avoir.
    private readonly HashSet<string> _enCours = [];
    private readonly object _verrou = new();

    /// <summary>Répond à une requête, sans jamais toucher au réseau d'écoute.</summary>
    public async Task<ReponseHttp> RepondreAsync(
        string methode, string chemin, CancellationToken arret = default)
    {
        chemin = NormaliserChemin(chemin);

        if (methode == "GET" && chemin is "/" or "/index.html")
        {
            return ReponseHttp.Html(PageTableau.Html);
        }

        if (methode == "GET" && chemin == "/api/etat")
        {
            return ReponseHttp.Json(200, Serialiser(await EtatAsync(arret)));
        }

        if (methode == "GET" && chemin == "/api/factures")
        {
            return ReponseHttp.Json(200, Serialiser(await FacturesAsync(arret)));
        }

        if (chemin.StartsWith("/api/factures/", StringComparison.Ordinal)
            && chemin.EndsWith("/certifier", StringComparison.Ordinal))
        {
            if (methode != "POST")
            {
                // Un GET ne certifie pas. Une adresse qui certifierait à la
                // simple visite partirait au premier lien cliqué, au premier
                // préchargement du navigateur, au premier historique rouvert.
                return Erreur(405, "Cette adresse ne répond qu'à POST : une visite ne certifie pas.");
            }

            var piece = chemin["/api/factures/".Length..^"/certifier".Length];
            return await CertifierAsync(Uri.UnescapeDataString(piece), arret);
        }

        return Erreur(404, $"Rien à cette adresse : {chemin}");
    }

    /// <summary>Coupe la chaîne de requête et normalise la barre finale.</summary>
    internal static string NormaliserChemin(string chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return "/";

        var coupe = chemin.IndexOfAny(['?', '#']);
        if (coupe >= 0) chemin = chemin[..coupe];

        if (chemin.Length > 1 && chemin.EndsWith('/')) chemin = chemin.TrimEnd('/');
        return chemin.Length == 0 ? "/" : chemin;
    }

    private static string Serialiser<T>(T valeur) => JsonSerializer.Serialize(valeur, Format);

    private static ReponseHttp Erreur(int code, string message) =>
        ReponseHttp.Json(code, JsonSerializer.Serialize(new { message }, Format));

    private InvoiceQuery Requete() => new()
    {
        Depuis = DateTime.Today.AddDays(-Math.Max(1, _reglages.FenetreJours)),
        Limite = Math.Max(1, _reglages.LimiteParTour),
    };

    private async Task<IReadOnlyList<LigneTableau>> FacturesAsync(CancellationToken arret)
    {
        using var portee = fabrique.CreateScope();
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();

        var lot = await lecteur.ReadAsync(Requete(), arret);

        // Le moteur du service, avec ses deux mémoires partagées : l'écran
        // affiche donc le compte à rebours réel de la stabilité et l'attente
        // réelle après un refus, pas une simulation qui repartirait de zéro à
        // chaque rafraîchissement de la page.
        var moteur = new MoteurSurveillance(lecteur, stabilite, _reglages.Mode, refus);

        return [.. lot.Conversions
            .Select(conversion => Traduire(conversion, moteur.Decider(conversion)))
            .OrderByDescending(ligne => ligne.Date)
            .ThenByDescending(ligne => ligne.Piece)];
    }

    private static LigneTableau Traduire(InvoiceConversion conversion, DecisionAgent decision)
    {
        var entete = conversion.Header;

        return new LigneTableau(
            Piece: entete.Piece,
            Identite: entete.Identite,
            Date: entete.Date.ToString("yyyy-MM-dd"),
            Client: entete.Tiers,
            ClientNom: conversion.Customer?.Intitule ?? "",
            ClientNcc: conversion.Customer?.Identifiant ?? "",
            TotalHT: conversion.TotalHT,
            TotalTTC: conversion.TotalTTC,
            Etat: conversion.Etat.ToString(),
            LibelleEtat: conversion.LibelleEtat,
            Motif: decision.Motif.ToString(),
            Explication: decision.Explication,

            // La seule condition du bouton, et c'est celle de l'expéditeur
            // lui-même. Ni la stabilité ni le mode n'entrent ici : le clic est
            // précisément ce qu'ils remplaçaient.
            Certifiable: conversion.Etat == EtatPiece.ACertifier,

            ReferenceFne: conversion.Certification?.ReferenceFne ?? "",
            Constats: [.. conversion.Report.Constats.Select(constat =>
                new ConstatTableau(
                    constat.Code, constat.Message, constat.Severite == Severite.Erreur))]);
    }

    private async Task<EtatTableau> EtatAsync(CancellationToken arret)
    {
        using var portee = fabrique.CreateScope();
        var depot = portee.ServiceProvider.GetRequiredService<ISageInvoiceRepository>();
        var fne = portee.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<FneOptions>>().Value;
        var lignes = await FacturesAsync(arret);
        var essai = await sonde.EprouverAsync(arret);

        return new EtatTableau(
            Mode: _reglages.Mode.ToString(),
            Environnement: api.EstTest ? "TEST" : "PRODUCTION",
            BaseUrl: api.BaseUrl,
            SurDonneesReelles: depot is SageInvoiceRepository,
            PlateformeJoignable: essai.Joignable,
            PlateformeExplication: essai.Explication,
            FenetreJours: _reglages.FenetreJours,
            DemarrageLe: "",
            PointDeVente: fne.PointOfSale,
            Etablissement: fne.Establishment,
            IdentiteRenseignee: FneCompleteness.IdentiteAControler(new SageFne.Core.Models.Fne.FneInvoice
            {
                PointOfSale = fne.PointOfSale,
                Establishment = fne.Establishment,
            }).Count == 0,
            Total: lignes.Count,
            Certifiables: lignes.Count(ligne => ligne.Certifiable),
            Certifiees: lignes.Count(ligne => ligne.Etat == nameof(EtatPiece.DejaCertifiee)),
            Bloquees: lignes.Count(ligne => ligne.Etat == nameof(EtatPiece.Bloquee)),
            Lu: DateTimeOffset.Now.ToString("HH:mm:ss"));
    }

    private async Task<ReponseHttp> CertifierAsync(string piece, CancellationToken arret)
    {
        piece = piece.Trim();

        if (piece.Length == 0 || piece.Length > 64)
        {
            return Erreur(400, "Numéro de pièce absent ou invraisemblable.");
        }

        lock (_verrou)
        {
            if (!_enCours.Add(piece))
            {
                return Erreur(409,
                    $"Un envoi de la pièce {piece} est déjà en cours. Attendez sa réponse : " +
                    "deux envois feraient deux factures chez la DGI, et une facture certifiée " +
                    "ne s'annule pas.");
            }
        }

        try
        {
            // La joignabilité s'éprouve AVANT d'entrer dans le chemin d'envoi,
            // comme dans le tour du service. Une fois le POST parti, plus rien
            // ne distingue une coupure survenue avant de celle survenue après :
            // la pièce reste en Sending et ne repartira jamais toute seule.
            if (!await sonde.JoignableAsync(arret))
            {
                var essai = await sonde.EprouverAsync(arret);
                return Erreur(503,
                    $"Rien n'a été envoyé : {essai.Explication} La pièce {piece} est intacte " +
                    "et reste certifiable dès que la plateforme répond.");
            }

            using var portee = fabrique.CreateScope();
            var expediteur = portee.ServiceProvider.GetRequiredService<InvoiceSender>();

            logger.LogInformation(
                "Tableau de bord : certification de la pièce {Piece} demandée à la main.", piece);

            var resultat = await expediteur.EnvoyerAsync(piece, confirme: true, arret);

            if (resultat.Reussi)
            {
                stabilite.Oublier(resultat.Conversion?.Header.Identite ?? piece);
                logger.LogInformation("Pièce {Piece} certifiée depuis le tableau. {Message}",
                    piece, resultat.Message);
            }
            else
            {
                logger.LogWarning("Pièce {Piece} non certifiée depuis le tableau : {Etat}. {Message}",
                    piece, resultat.Etat, resultat.Message);
            }

            return ReponseHttp.Json(resultat.Reussi ? 200 : 422, Serialiser(new ResultatCertification(
                Reussi: resultat.Reussi,
                Piece: piece,
                Etat: resultat.Etat.ToString(),
                Message: resultat.Message,
                ReferenceFne: resultat.Reponse?.ReferenceFne ?? "",
                CodeHttp: resultat.Reponse?.CodeHttp,

                // Le corps brut, tel quel. Le reformuler reviendrait à
                // interpréter un message dont nous ne connaissons pas encore le
                // vocabulaire — et c'est précisément ce vocabulaire qu'on
                // cherche à apprendre.
                ReponsePlateforme: resultat.Reponse?.CorpsBrut ?? "")));
        }
        finally
        {
            lock (_verrou) _enCours.Remove(piece);
        }
    }
}
