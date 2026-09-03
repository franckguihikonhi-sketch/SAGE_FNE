using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using System.Text.Json.Serialization;
using SageFne.Core.Models.Sage;
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
    Certification.ICertificateur certificateur)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AgentOptions _reglages = reglages.Value;

    /// <summary>Change à chaque compilation du binaire qui sert la page.</summary>
    private static readonly string Empreinte =
        typeof(RouteurTableau).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    // Les envois en cours, par pièce. Un double-clic sur « Certifier » lance
    // deux appels concurrents : le premier inscrit Sending, mais le second a
    // déjà lu le registre avant cette inscription et part quand même. Deux POST,
    // deux factures chez la DGI, et rien pour les défaire. C'est arrivé une fois
    // par une autre voie — la pièce 1072 — et il a fallu un avoir.

    /// <summary>Répond à une requête, sans jamais toucher au réseau d'écoute.</summary>
    public async Task<ReponseHttp> RepondreAsync(
        string methode, string chemin, string corps = "", CancellationToken arret = default)
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

        if (methode == "GET" && chemin == "/api/modes-paiement")
        {
            return ReponseHttp.Json(200, Serialiser(ModePaiementFne.Tous));
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
            return await CertifierAsync(Uri.UnescapeDataString(piece), corps, arret);
        }

        return Erreur(404, $"Rien à cette adresse : {chemin}");
    }

    private static DemandeCertification? LireDemande(string corps)
    {
        if (string.IsNullOrWhiteSpace(corps)) return null;

        try
        {
            return JsonSerializer.Deserialize<DemandeCertification>(corps, Format);
        }
        catch (JsonException)
        {
            // Un corps illisible n'est pas un choix : la suite refusera, en
            // disant que le mode n'a pas été choisi. C'est exact.
            return null;
        }
    }

    /// <summary>Le compte tiers d'une pièce, pour y rattacher le mode retenu.</summary>
    private async Task<string?> CompteTiersAsync(
        string piece, short domaine, CancellationToken arret)
    {
        using var portee = fabrique.CreateScope();
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();
        var lot = await lecteur.ReadAsync(
            InvoiceQuery.Piece(piece) with { Domaine = domaine }, arret);

        var tiers = lot.Conversions.FirstOrDefault()?.Header.Tiers;
        return string.IsNullOrWhiteSpace(tiers) ? null : tiers;
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

    /// <summary>
    /// L'identité du dossier auprès de la DGI est-elle renseignée.
    /// </summary>
    /// <remarks>
    /// Elle conditionne <b>tout</b> envoi, sans qu'aucune facture n'y soit pour
    /// quoi que ce soit. Sans elle, l'expéditeur refuse et la DGI refuserait de
    /// toute façon — « Establishment is invalid ».
    /// </remarks>
    private bool IdentitePosee()
    {
        using var portee = fabrique.CreateScope();
        var fne = portee.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<FneOptions>>().Value;

        return FneCompleteness.IdentiteAControler(new SageFne.Core.Models.Fne.FneInvoice
        {
            PointOfSale = fne.PointOfSale,
            Establishment = fne.Establishment,
        }).Count == 0;
    }

    private InvoiceQuery Requete() => new()
    {
        Depuis = DateTime.Today.AddDays(-Math.Max(1, _reglages.FenetreJours)),
        Limite = Math.Max(1, _reglages.LimiteParTour),
    };

    private async Task<IReadOnlyList<LigneTableau>> FacturesAsync(CancellationToken arret)
    {
        using var portee = fabrique.CreateScope();
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();

        // Les deux domaines, dans la même liste. Deux lectures et non une : le
        // domaine est un critère de la requête, et l'élargir en un « où domaine
        // in (0,1) » ferait rentrer les achats dans tous les autres chemins qui
        // partagent cette requête.
        var lot = await lecteur.ReadAsync(Requete(), arret);
        var achats = await lecteur.ReadAsync(
            Requete() with { Domaine = SageDomaines.Achat }, arret);

        // Le moteur du service, avec ses deux mémoires partagées : l'écran
        // affiche donc le compte à rebours réel de la stabilité et l'attente
        // réelle après un refus, pas une simulation qui repartirait de zéro à
        // chaque rafraîchissement de la page.
        var moteur = new MoteurSurveillance(lecteur, stabilite, _reglages.Mode, refus);

        // Sans identité DGI, rien ne peut partir — et un bouton actif qui échoue
        // à tous les coups vaut moins que pas de bouton. L'écran annonçait
        // « 4 prêtes à certifier » juste au-dessus de « aucune facture ne peut
        // être certifiée » : deux affirmations contraires, sur le même écran.
        var identite = IdentitePosee();

        var modes = await portee.ServiceProvider
            .GetRequiredService<IModesPaiementClients>().ToutAsync(arret);

        var parDefaut = portee.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<FneOptions>>()
            .Value.PaymentMethod;

        return [.. lot.Conversions.Concat(achats.Conversions)
            .Select(conversion => Traduire(
                conversion, moteur.Decider(conversion), identite, modes, parDefaut))
            .OrderByDescending(ligne => ligne.Date)
            .ThenByDescending(ligne => ligne.Piece)];
    }

    private static LigneTableau Traduire(
        InvoiceConversion conversion,
        DecisionAgent decision,
        bool identitePosee,
        IReadOnlyDictionary<string, string> modesParClient,
        string modeParDefaut)
    {
        // Ce qui partira réellement : le choix du client s'il existe, sinon le
        // paramétrage. La distinction est affichée, parce qu'un mode supposé et
        // un mode choisi ne s'engagent pas de la même façon.
        var choisi = modesParClient.TryGetValue(conversion.Header.Tiers, out var retenu)
            ? ModePaiementFne.Normaliser(retenu)
            : null;

        var effectif = choisi ?? modeParDefaut;

        var entete = conversion.Header;

        return new LigneTableau(
            Piece: entete.Piece,
            Identite: entete.Identite,
            Domaine: SageDomaines.Libelle(entete.Domaine),
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
            Certifiable: conversion.Etat == EtatPiece.ACertifier && identitePosee,
            ModePaiement: effectif,
            ModePaiementLibelle: ModePaiementFne.Libelle(effectif),
            ModePaiementChoisi: choisi is not null,

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
            // Renseigné, enfin. Il valait la chaîne vide depuis le premier
            // jour : l'écran ne pouvait pas dire à partir de quand une facture
            // entre dans le périmètre, et c'est la première question qu'on se
            // pose devant une liste où rien n'est à faire.
            DemarrageLe: fne.DemarrageLe?.ToString("yyyy-MM-dd") ?? "",
            Build: Empreinte,
            PointDeVente: fne.PointOfSale,
            Etablissement: fne.Establishment,
            IdentiteRenseignee: IdentitePosee(),
            Total: lignes.Count,
            Certifiables: lignes.Count(ligne => ligne.Certifiable),
            Certifiees: lignes.Count(ligne => ligne.Etat == nameof(EtatPiece.DejaCertifiee)),
            Bloquees: lignes.Count(ligne => ligne.Etat == nameof(EtatPiece.Bloquee)),
            Lu: DateTimeOffset.Now.ToString("HH:mm:ss"));
    }

    /// <summary>Ce que le navigateur envoie avec le clic.</summary>
    private sealed record DemandeCertification(
        [property: JsonPropertyName("modePaiement")] string? ModePaiement,

        // La liste porte les deux domaines : sans lui, une pièce d'achat serait
        // cherchée parmi les ventes et déclarée introuvable.
        [property: JsonPropertyName("domaine")] string? Domaine);

    private async Task<ReponseHttp> CertifierAsync(
        string piece, string corps, CancellationToken arret)
    {
        piece = piece.Trim();

        if (piece.Length == 0 || piece.Length > 64)
        {
            return Erreur(400, "Numéro de pièce absent ou invraisemblable.");
        }

        // Le mode de règlement, exigé avant tout envoi. La DGI le marque
        // obligatoire, et jusqu'ici toutes les factures partaient avec la
        // valeur du paramétrage — « à terme » — vraie ou fausse. Une facture
        // certifiée qui déclare un mode de paiement inexact ne se corrige que
        // par un avoir.
        var demande = LireDemande(corps);
        var mode = ModePaiementFne.Normaliser(demande?.ModePaiement);

        var domaine = string.Equals(demande?.Domaine, "achat", StringComparison.OrdinalIgnoreCase)
            ? SageDomaines.Achat
            : SageDomaines.Vente;

        if (mode is null)
        {
            return Erreur(400,
                $"Rien n'a été envoyé : le mode de règlement de la pièce {piece} n'a pas été " +
                "choisi. La DGI l'exige, et Sage ne le porte pas — c'est à vous de le dire.");
        }

        // Un seul chemin d'envoi pour les deux écrans — le tableau local et la
        // demande venue du SaaS. Sonde, verrou, mode retenu avant l'envoi : ce
        // qui vit dans le Certificateur ne peut plus manquer au second
        // appelant, et un second appelant est exactement ce qui a révélé les
        // sept défauts de cette forme.
        var issue = await certificateur.CertifierAsync(
            piece, mode, domaine, "Tableau de bord", arret);

        if (!issue.Reussi && issue.Etat is null)
        {
            // Rien n'est parti : verrou déjà pris, ou plateforme injoignable.
            // Le code HTTP distingue les deux pour l'écran.
            var code = certificateur.EnCours(piece) ? 409 : 503;
            return Erreur(code, issue.Message);
        }

        return ReponseHttp.Json(issue.Reussi ? 200 : 422, Serialiser(new ResultatCertification(
            Reussi: issue.Reussi,
            Piece: piece,
            Etat: issue.Etat?.ToString() ?? "",
            Message: issue.Message,
            ReferenceFne: issue.ReferenceFne,
            CodeHttp: issue.CodeHttp,
            ReponsePlateforme: issue.ReponsePlateforme)));
    }
}
