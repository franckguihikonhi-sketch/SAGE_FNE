using Microsoft.Extensions.Logging;

namespace SageFne.Agent.Journalisation;

/// <summary>
/// Écrit le journal dans un fichier, un jour par fichier.
/// </summary>
/// <remarks>
/// Un service Windows n'a pas de console où écrire — et l'exigence est
/// justement qu'aucune fenêtre n'apparaisse. Sans fichier, l'agent serait
/// entièrement muet : la seule façon de savoir qu'il a bloqué une facture
/// serait de constater son absence chez la DGI.
///
/// Le journal vit dans les données d'application, jamais à côté du binaire :
/// un registre a déjà été perdu dans <c>bin\Debug</c>, effacé par un
/// <c>dotnet clean</c>.
///
/// Rien de secret n'y entre. La clé d'API ne traverse aucun de ces messages, et
/// <see cref="Sante.Heartbeat"/> ne porte ni adresse ni nom de client.
/// </remarks>
public sealed class JournalFichier : ILoggerProvider
{
    private readonly string _dossier;
    private readonly int _retentionJours;
    private readonly object _verrou = new();

    private static readonly System.Text.UTF8Encoding Utf8AvecBom = new(encoderShouldEmitUTF8Identifier: true);
    private DateOnly _jourCourant;

    public JournalFichier(string dossier, int retentionJours = 30)
    {
        _dossier = dossier;
        _retentionJours = Math.Max(1, retentionJours);
        Directory.CreateDirectory(_dossier);
        Purger();
        EcarterUnFichierSansBom();
    }

    /// <summary>
    /// Met de côté un fichier du jour écrit sans BOM, pour repartir sur un
    /// lisible.
    /// </summary>
    /// <remarks>
    /// <c>File.AppendAllText</c> n'écrit le préambule qu'à la création. Un
    /// fichier laissé par une version antérieure resterait donc illisible pour
    /// toujours, et chaque ligne ajoutée avec lui — c'est exactement ce qui est
    /// arrivé sur le premier poste où l'agent a tourné.
    ///
    /// L'ancien n'est pas effacé : il est renommé. Un journal se met de côté, il
    /// ne se jette pas.
    /// </remarks>
    private void EcarterUnFichierSansBom()
    {
        try
        {
            var fichier = FichierDuJour;
            if (!File.Exists(fichier)) return;

            using (var flux = File.OpenRead(fichier))
            {
                Span<byte> tete = stackalloc byte[3];
                if (flux.Read(tete) == 3 && tete[0] == 0xEF && tete[1] == 0xBB && tete[2] == 0xBF)
                {
                    return;
                }
            }

            var ecarte = Path.ChangeExtension(fichier, null) + "-avant-bom.log";
            if (File.Exists(ecarte)) File.Delete(ecarte);
            File.Move(fichier, ecarte);
        }
        catch (Exception)
        {
            // Un journal qui ne peut pas se ranger ne doit pas arrêter l'agent.
        }
    }

    public ILogger CreateLogger(string categorie) => new Ecrivain(this, categorie);

    public void Dispose() { }

    /// <summary>Le fichier du jour.</summary>
    public string FichierDuJour =>
        Path.Combine(_dossier, $"agent-{DateTime.Now:yyyy-MM-dd}.log");

    private void Ecrire(string ligne)
    {
        lock (_verrou)
        {
            // Le changement de jour est le seul moment où purger : le faire à
            // chaque ligne coûterait un parcours de dossier par message.
            var aujourdhui = DateOnly.FromDateTime(DateTime.Now);
            if (aujourdhui != _jourCourant)
            {
                _jourCourant = aujourdhui;
                Purger();
            }

            try
            {
                // Avec BOM : Windows PowerShell lit un fichier sans BOM en ANSI
                // et rend « Vérification » en « VÃ©rification ». Un journal
                // illisible ne se lit pas, et c'est le seul endroit où l'agent
                // parle.
                File.AppendAllText(FichierDuJour, ligne + Environment.NewLine, Utf8AvecBom);
            }
            catch (IOException)
            {
                // Un journal qui n'écrit pas ne doit pas arrêter l'agent : ce
                // serait faire d'un disque plein une panne de certification.
                // La perte se verra au heartbeat, qui passe par ailleurs.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Purger()
    {
        try
        {
            var limite = DateTime.Now.AddDays(-_retentionJours);
            foreach (var fichier in Directory.EnumerateFiles(_dossier, "agent-*.log"))
            {
                if (File.GetLastWriteTime(fichier) < limite) File.Delete(fichier);
            }
        }
        catch (Exception)
        {
            // Le ménage est un confort, pas une garantie.
        }
    }

    /// <summary>Une ligne par événement, préfixée de quoi la retrouver.</summary>
    private sealed class Ecrivain(JournalFichier journal, string categorie) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel niveau) => niveau >= LogLevel.Information;

        public void Log<TState>(
            LogLevel niveau, EventId evenement, TState state, Exception? erreur,
            Func<TState, Exception?, string> formater)
        {
            if (!IsEnabled(niveau)) return;

            var message = formater(state, erreur);
            var ligne = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Abreger(niveau)}] " +
                        $"{Court(categorie)} {message}";

            if (erreur is not null) ligne += $"{Environment.NewLine}    {erreur}";

            journal.Ecrire(ligne);
        }

        private static string Abreger(LogLevel niveau) => niveau switch
        {
            LogLevel.Critical => "CRIT",
            LogLevel.Error => "ERRE",
            LogLevel.Warning => "AVER",
            LogLevel.Information => "INFO",
            _ => "TRAC",
        };

        /// <summary>Le dernier segment suffit : le reste est du bruit répété.</summary>
        private static string Court(string categorie) =>
            categorie[(categorie.LastIndexOf('.') + 1)..];
    }
}
