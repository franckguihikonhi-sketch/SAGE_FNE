using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Mapping;

namespace SageFne.Core.Configuration;

/// <summary>
/// Le câblage du middleware, en un seul endroit.
/// </summary>
/// <remarks>
/// Extrait de <c>Program</c> pour une raison précise : tant qu'il vivait dans
/// des instructions de haut niveau, aucun test ne pouvait construire le
/// conteneur. Une interface enregistrée sous son type concret passait alors
/// toutes les vérifications et n'échouait qu'à l'exécution, devant
/// l'utilisateur. C'est arrivé avec <see cref="IFneApiClient"/>.
/// </remarks>
public static class ServicesMiddleware
{
    /// <param name="chaineSage">
    /// Chaîne de connexion Sage. Vide ou laissée au gabarit : le jeu d'essai
    /// prend la place, et rien ne parle à SQL Server.
    /// </param>
    /// <param name="cheminRegistre">
    /// Fichier du registre des certifications. Null : registre en mémoire.
    /// </param>
    public static IServiceCollection AjouterMiddlewareFne(
        this IServiceCollection services,
        IConfiguration configuration,
        string chaineSage,
        string? cheminRegistre)
    {
        services.Configure<FneOptions>(configuration.GetSection(FneOptions.Section));

        // L'API de la DGI, liée sur la même section que le reste : la clé se
        // pose en « Fne:ApiKey », dans les secrets utilisateur et nulle part
        // ailleurs.
        var api = new FneApiOptions();
        configuration.GetSection(FneOptions.Section).Bind(api);
        services.AddSingleton(api);

        // Sous l'interface, et pas seulement sous le type concret : c'est
        // l'interface que réclame InvoiceSender.
        services.AddHttpClient<IFneApiClient, FneApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(api.TimeoutSeconds, 5, 300));
        });

        services.AddSingleton<InvoiceSender>();
        services.AddSingleton<IFneInvoiceMapper, FneInvoiceMapper>();
        services.AddSingleton<InvoiceBatchReader>();

        if (ConnexionRenseignee(chaineSage))
        {
            services.AddSingleton<ISageInvoiceRepository>(fournisseur =>
                new SageInvoiceRepository(
                    chaineSage, fournisseur.GetRequiredService<ILogger<SageInvoiceRepository>>()));
        }
        else
        {
            services.AddSingleton<ISageInvoiceRepository, DemoSageInvoiceRepository>();
        }

        // Les deux dépôts savent aussi explorer : même instance, deux rôles.
        services.AddSingleton<ISageTaxInspector>(fournisseur =>
            (ISageTaxInspector)fournisseur.GetRequiredService<ISageInvoiceRepository>());

        // Le registre des certifications vit hors de Sage : la base y est en
        // lecture seule, et rien n'y prévoit de zone pour la référence FNE.
        if (cheminRegistre is not null)
        {
            services.AddSingleton<ICertificationLedger>(fournisseur =>
                new JsonCertificationLedger(
                    cheminRegistre, fournisseur.GetRequiredService<ILogger<JsonCertificationLedger>>()));
        }
        else
        {
            services.AddSingleton<ICertificationLedger, DemoCertificationLedger>();
        }

        // Les règles de TVA à 0 % vivent à côté du registre des certifications :
        // même durée de vie, même exigence de trace. Elles n'ont pas leur place
        // dans appsettings.json, qui ne sait pas porter une preuve.
        // La politique lit le registre une fois, au démarrage : chaque exécution
        // est un processus neuf, et une relecture par ligne serait du gaspillage.
        services.AddSingleton<Mapping.IZeroVatPolicy>(fournisseur =>
        {
            var registre = fournisseur.GetRequiredService<Regles.RegistreRegles>();
            var reglages = fournisseur.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<FneOptions>>().Value;

            return new Regles.RegistreZeroVatPolicy(
                registre.CourantesAsync().GetAwaiter().GetResult(),
                reglages.ZeroVat,
                DateTimeOffset.Now);
        });

        services.AddSingleton(fournisseur => new Regles.RegistreRegles(
            CheminRegles(cheminRegistre),
            fournisseur.GetRequiredService<ILogger<Regles.RegistreRegles>>()));

        return services;
    }

    /// <summary>
    /// Le registre des règles, à côté de celui des certifications.
    /// </summary>
    /// <remarks>
    /// Les deux se sauvegardent ensemble ou se perdent ensemble : une règle sans
    /// les factures qu'elle a classées, ou l'inverse, ne vaut pas grand-chose à
    /// l'audit.
    /// </remarks>
    public static string CheminRegles(string? cheminCertifications) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(cheminCertifications ?? CheminDurable()))
                ?? AppContext.BaseDirectory,
            "regles-tva-zero.json");

    /// <summary>Nom du fichier de registre, partout où il est écrit.</summary>
    public const string NomDuRegistre = "certifications.json";

    /// <summary>
    /// Où écrire le registre des certifications, ou null pour le garder en
    /// mémoire.
    /// </summary>
    /// <remarks>
    /// Le vide compte comme absent. <c>appsettings.json</c> porte
    /// <c>"CertificationLedgerPath": ""</c>, et un simple <c>??</c> ne
    /// retombait pas sur le défaut : le registre recevait une chaîne vide, et
    /// <c>Path.GetFullPath("")</c> levait au moment d'écrire — c'est-à-dire au
    /// milieu du premier envoi.
    /// </remarks>
    public static string? CheminRegistre(
        string? demandeEnLigneDeCommande,
        string? configure,
        string dossierDeLExecutable,
        bool connexionSageConfiguree)
    {
        if (!string.IsNullOrWhiteSpace(demandeEnLigneDeCommande)) return demandeEnLigneDeCommande.Trim();
        if (!string.IsNullOrWhiteSpace(configure)) return configure.Trim();

        // Sans base réelle, rien ne mérite d'être écrit sur le disque : le jeu
        // d'essai garde son registre en mémoire.
        return connexionSageConfiguree ? CheminDurable() : null;
    }

    /// <summary>
    /// Le registre par défaut, dans les données d'application de l'utilisateur.
    /// </summary>
    /// <remarks>
    /// Il a d'abord été posé à côté de l'exécutable — c'est-à-dire dans
    /// <c>bin\Debug\net8.0\</c>. C'était une faute : ce dossier est une sortie
    /// de compilation, que <c>dotnet clean</c>, une suppression de <c>bin</c> ou
    /// un clone neuf effacent sans prévenir. Le registre est la seule mémoire
    /// d'une certification — Sage n'en porte aucune trace — et le perdre fait
    /// repartir vers la DGI des factures déjà certifiées.
    ///
    /// <c>%APPDATA%</c> sous Windows, <c>~/.config</c> ailleurs : ces dossiers
    /// survivent aux compilations, et sont sauvegardés par les outils courants.
    /// </remarks>
    public static string CheminDurable() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "SageFne",
        NomDuRegistre);

    /// <summary>
    /// L'ancien emplacement par défaut, à côté de l'exécutable.
    /// </summary>
    /// <remarks>
    /// Conservé pour que le diagnostic puisse aller y regarder : un registre
    /// écrit avant ce changement s'y trouve encore, et il vaut mieux le montrer
    /// à l'exploitant que le laisser disparaître en silence. Rien n'est déplacé
    /// automatiquement — un registre se déplace en connaissance de cause.
    /// </remarks>
    public static string AncienChemin(string dossierDeLExecutable) =>
        Path.Combine(dossierDeLExecutable, NomDuRegistre);

    /// <summary>
    /// Une chaîne restée au gabarit n'est pas une chaîne : mieux vaut le jeu
    /// d'essai, qui se déclare, qu'une tentative de connexion vers « SERVEUR_SQL ».
    /// </summary>
    public static bool ConnexionRenseignee(string? chaine) =>
        !string.IsNullOrWhiteSpace(chaine)
        && !chaine.Contains("SERVEUR_SQL", StringComparison.OrdinalIgnoreCase)
        && !chaine.Contains("MOT_DE_PASSE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ce qui empêche cette chaîne d'être une chaîne de connexion, ou null.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnexionRenseignee"/> dit seulement qu'une valeur a été
    /// posée. Elle ne dit pas qu'elle est lisible : du texte quelconque —
    /// une note collée à la place de la chaîne, un exemple recopié tel quel —
    /// passe ce contrôle et ne se voit qu'au premier appel à SQL Server, sous
    /// forme d'une trace de pile de quinze lignes.
    ///
    /// Le message renvoyé ici ne reprend jamais la chaîne : elle porte le mot
    /// de passe du compte de lecture, et un journal se lit par-dessus l'épaule.
    /// </remarks>
    public static string? ChaineIllisible(string? chaine)
    {
        if (string.IsNullOrWhiteSpace(chaine)) return null;

        try
        {
            _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(chaine);
        }
        catch (ArgumentException erreur)
        {
            return "La chaîne de connexion Sage n'est pas analysable : " + erreur.Message.Trim() +
                   " Attendu quelque chose comme « Server=POSTE\\SQLEXPRESS;Database=…;" +
                   "User Id=…;Password=MOT_DE_PASSE;TrustServerCertificate=True; ». " +
                   "La valeur n'est pas reproduite ici : elle porte un mot de passe.";
        }

        return null;
    }
}
