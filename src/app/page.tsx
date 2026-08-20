import { Converter } from "@/components/Converter";
import { PROFILES } from "@/lib/sage/profile";

export default function Home() {
  const profiles = PROFILES.map((profile) => ({
    id: profile.id,
    label: profile.label,
    description: profile.description,
  }));

  return (
    <main className="mx-auto max-w-6xl px-6 py-10">
      <header className="mb-8">
        <p className="text-sm font-semibold uppercase tracking-wide text-brand-600">
          FNE - DGI Cote d&apos;Ivoire
        </p>
        <h1 className="mt-1 text-3xl font-bold text-slate-900">
          Convertisseur FNE &rarr; Sage 100 Gestion Commerciale
        </h1>
        <p className="mt-2 max-w-3xl text-slate-600">
          Deposez l&apos;export de vos factures FNE (CSV, Excel ou JSON). L&apos;outil reconnait les
          colonnes, regroupe les lignes par facture, controle les totaux et genere le fichier texte
          a importer dans Sage via <em>Fichier &gt; Importer &gt; Format parametrable</em>.
        </p>
      </header>
      <Converter profiles={profiles} />
    </main>
  );
}
