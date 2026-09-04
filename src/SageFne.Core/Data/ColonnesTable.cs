namespace SageFne.Core.Data;

/// <summary>
/// Les colonnes réellement présentes dans une table du dossier.
/// </summary>
/// <remarks>
/// Écrire la liste des colonnes en dur revient à parier sur une version de
/// Sage. Le pari a été perdu : <c>DL_DocType</c> n'existe pas dans le dossier
/// HT, et toute la lecture des lignes échouait sur ce seul nom.
///
/// Le catalogue tranche avant la requête : ce qui est là est demandé, ce qui
/// manque est laissé de côté et signalé. Une colonne absente ne fait plus
/// tomber la lecture entière — sauf si elle est indispensable, auquel cas
/// l'erreur nomme la colonne plutôt que de laisser SQL Server s'en charger.
/// </remarks>
internal sealed class ColonnesTable(string table, IReadOnlySet<string> presentes)
{
    public string Table => table;
    public IReadOnlySet<string> Presentes => presentes;

    public bool A(string colonne) => presentes.Contains(colonne);

    /// <summary>Liste de sélection, réduite à ce qui existe.</summary>
    public string Selection(string alias, IEnumerable<string> souhaitees) =>
        string.Join(", ", souhaitees.Where(A).Select(colonne => $"{alias}.{colonne}"));

    public IReadOnlyList<string> Absentes(IEnumerable<string> souhaitees) =>
        souhaitees.Where(colonne => !A(colonne)).ToList();

    /// <summary>
    /// Sans ces colonnes-là, il n'y a pas de facture à traduire : mieux vaut
    /// une erreur qui les nomme qu'un montant faux.
    /// </summary>
    public void Exiger(IEnumerable<string> indispensables)
    {
        var manquantes = Absentes(indispensables);
        if (manquantes.Count == 0) return;

        throw new InvalidOperationException(
            $"La table {table} du dossier ne porte pas {string.Join(", ", manquantes)}. " +
            "Ces colonnes sont indispensables à la lecture des factures : " +
            "vérifiez que la base est bien un dossier commercial Sage 100.");
    }
}
