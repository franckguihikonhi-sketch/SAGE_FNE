namespace SageFne.Core.Saas;

/// <summary>
/// Ce qu'il faut pour publier vers la base d'audit, et rien de plus.
/// </summary>
/// <remarks>
/// Le miroir est <b>inerte tant qu'il n'est pas configuré</b>. C'est délibéré :
/// un poste qui certifie aujourd'hui doit continuer exactement comme avant, sans
/// qu'aucune de ces valeurs soit renseignée. Le SaaS s'ajoute, il ne s'impose
/// pas.
///
/// La clé de service ne vit jamais dans <c>appsettings.json</c>, jamais dans le
/// journal, jamais dans Git — même traitement que la clé FNE, pour la même
/// raison : elle donne un accès complet à la base.
/// </remarks>
public sealed class OptionsSaas
{
    public const string Section = "Saas";

    /// <summary>L'adresse du projet Supabase, sans chemin.</summary>
    public string Url { get; set; } = "";

    /// <summary>La clé de service. Secret : variable machine ou user-secrets.</summary>
    public string CleService { get; set; } = "";

    /// <summary>Le dossier auquel ces certifications appartiennent.</summary>
    public string DossierId { get; set; } = "";

    /// <summary>Secondes avant d'abandonner une publication.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Vrai quand les trois valeurs indispensables sont posées.
    /// </summary>
    /// <remarks>
    /// Les trois, et pas une seule : publier vers une URL sans clé, ou avec une
    /// clé sans dossier, ne produirait que des refus répétés au journal. Mieux
    /// vaut rester silencieux tant que la configuration est incomplète.
    /// </remarks>
    public bool Actif =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(CleService)
        && !string.IsNullOrWhiteSpace(DossierId)
        && !Validation.MarqueurGabarit.Est(Url)
        && !Validation.MarqueurGabarit.Est(DossierId);

    /// <summary>La clé, montrable dans un diagnostic sans la révéler.</summary>
    public string CleMasquee() => CleService.Length <= 8
        ? new string('•', CleService.Length)
        : $"{CleService[..4]}{new string('•', 8)}{CleService[^4..]}";

    /// <summary>L'adresse d'une table, côté PostgREST.</summary>
    public Uri AdresseTable(string table) =>
        new(new Uri(Url.TrimEnd('/') + "/"), $"rest/v1/{table}");

    /// <summary>L'adresse de la table des certifications.</summary>
    public Uri AdresseCertifications() => AdresseTable("certifications");
}
