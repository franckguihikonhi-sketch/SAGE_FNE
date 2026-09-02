using SageFne.Core.Configuration;
using SageFne.Core.Mapping;

namespace SageFne.Core.Regles;

/// <summary>
/// La politique adossée au registre des règles.
/// </summary>
/// <remarks>
/// Le registre décide, et lui seul. Ce qui reste dans <c>appsettings.json</c>
/// est encore lu, mais <b>traité comme un brouillon</b> : la ligne est bloquée,
/// et le message dit quelle commande promeut cette déclaration en règle validée.
///
/// Cela peut surprendre — un paramétrage qui cesse de produire un code. C'est
/// délibéré : le paramétrage dit quel code envoyer, sans dire qui l'a autorisé
/// ni sur quelle preuve. Or ce code part sur des factures définitives. Une
/// déclaration sans preuve doit donc bloquer, pas certifier.
///
/// L'ordre reste celui qu'on connaît : régime de l'acheteur, article, famille,
/// client, dossier. Et rien de tout cela n'est consulté sur une ligne qui porte
/// un taux : seule une ligne déjà constatée à 0 % arrive ici.
/// </remarks>
public sealed class RegistreZeroVatPolicy(
    IReadOnlyDictionary<string, RegleZeroVat> regles,
    ZeroVatOptions heritage,
    DateTimeOffset quand) : IZeroVatPolicy
{
    /// <summary>La règle qui a décidé, pour la trace d'audit.</summary>
    public RegleZeroVat? Derniere { get; private set; }

    public ZeroVatDecision Decider(ZeroVatContexte contexte)
    {
        Derniere = null;

        foreach (var (portee, cle) in new[]
                 {
                     (PorteeRegle.RegimeAcheteur, contexte.CtNum),
                     (PorteeRegle.Article, contexte.ArticleReference),
                     (PorteeRegle.Famille, contexte.Famille),
                     (PorteeRegle.Client, contexte.CtNum),
                     (PorteeRegle.Dossier, ""),
                 })
        {
            if (portee != PorteeRegle.Dossier && string.IsNullOrWhiteSpace(cle)) continue;
            if (!regles.TryGetValue($"{portee}/{cle.Trim()}".ToUpperInvariant(), out var regle)) continue;

            Derniere = regle;
            var ou = Nommer(portee, cle);

            if (regle.Empechement(quand) is { } pourquoi)
            {
                return new ZeroVatDecision(
                    CodeTvaZero.Inconnu,
                    $"{ou} — {regle.Reperage}",
                    $"la règle {regle.Reperage} ({ou}) ne s'applique pas : {pourquoi}.",
                    regle.Fondement);
            }

            return new ZeroVatDecision(regle.Code, $"{ou} — {regle.Reperage}", Fondement: regle.Fondement);
        }

        // Rien au registre : le paramétrage hérité, s'il dit quelque chose, le
        // dit sans preuve. Il est donc rapporté, et il bloque.
        return Brouillon(contexte) ?? new ZeroVatDecision(
            CodeTvaZero.Inconnu,
            "aucune règle applicable");
    }

    private ZeroVatDecision? Brouillon(ZeroVatContexte contexte)
    {
        foreach (var (portee, cle, valeur) in new[]
                 {
                     (PorteeRegle.RegimeAcheteur, contexte.CtNum, Lire(heritage.CustomerTaxRegimes, contexte.CtNum)),
                     (PorteeRegle.Article, contexte.ArticleReference, Lire(heritage.ByArticle, contexte.ArticleReference)),
                     (PorteeRegle.Famille, contexte.Famille, Lire(heritage.ByFamily, contexte.Famille)),
                     (PorteeRegle.Client, contexte.CtNum, Lire(heritage.ByCustomer, contexte.CtNum)),
                     (PorteeRegle.Dossier, "", heritage.Default is "Unknown" or "" ? null : heritage.Default),
                 })
        {
            if (valeur is null) continue;

            var ou = Nommer(portee, cle);
            var commande = portee switch
            {
                PorteeRegle.RegimeAcheteur => $"zero-vat-regle client {cle} --regime RME --code Tvad …",
                PorteeRegle.Article => $"zero-vat-regle article {cle} --code Tvad …",
                PorteeRegle.Famille => $"zero-vat-regle famille {cle} --code Tvad …",
                PorteeRegle.Client => $"zero-vat-regle client {cle} --code Tvac …",
                _ => "zero-vat-regle dossier --code Tvac …",
            };

            return new ZeroVatDecision(
                CodeTvaZero.Inconnu,
                $"{ou} — paramétrage hérité",
                $"{ou} est déclaré « {valeur} » dans le paramétrage, mais aucune règle validée ne " +
                $"le porte au registre. Une déclaration sans preuve ne certifie pas : promouvez-la " +
                $"par « {commande} », en indiquant qui l'a validée et sur quel document.");
        }

        return null;
    }

    private static string? Lire(IReadOnlyDictionary<string, string> table, string cle) =>
        !string.IsNullOrWhiteSpace(cle) && table.TryGetValue(cle.Trim(), out var valeur)
        && !string.IsNullOrWhiteSpace(valeur) && valeur.Trim() != "Unknown"
            ? valeur
            : null;

    private static string Nommer(PorteeRegle portee, string cle) => portee switch
    {
        PorteeRegle.RegimeAcheteur => $"régime acheteur du client {cle}",
        PorteeRegle.Article => $"article {cle}",
        PorteeRegle.Famille => $"famille {cle}",
        PorteeRegle.Client => $"client {cle}",
        _ => "dossier",
    };
}
