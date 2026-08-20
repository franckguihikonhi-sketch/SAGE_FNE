"use client";

import { useMemo, useRef, useState } from "react";
import type { ConvertResult } from "@/lib/pipeline";
import { FIELD_LABELS, type FneField } from "@/lib/fne/fields";

interface ProfileOption {
  id: string;
  label: string;
  description: string;
}

const money = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 2 });

export function Converter({ profiles }: { profiles: ProfileOption[] }) {
  const [file, setFile] = useState<File | null>(null);
  const [profileId, setProfileId] = useState(profiles[0]?.id ?? "");
  const [customers, setCustomers] = useState("");
  const [compteParDefaut, setCompteParDefaut] = useState("");
  const [reglements, setReglements] = useState("");
  const [numeroPiece, setNumeroPiece] = useState("sequence");
  const [result, setResult] = useState<ConvertResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const selectedProfile = profiles.find((profile) => profile.id === profileId);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (!file) {
      setError("Selectionnez d'abord un export FNE.");
      return;
    }
    setLoading(true);
    setError(null);
    setResult(null);

    const form = new FormData();
    form.set("file", file);
    form.set("profileId", profileId);
    form.set("customers", customers);
    form.set("compteParDefaut", compteParDefaut);
    form.set("reglements", reglements);
    form.set("numeroPiece", numeroPiece);

    try {
      const response = await fetch("/api/convert", { method: "POST", body: form });
      const payload = await response.json();
      if (!response.ok) {
        setError(payload.error ?? "La conversion a echoue.");
        return;
      }
      setResult(payload as ConvertResult);
    } catch {
      setError("Impossible de joindre le serveur de conversion.");
    } finally {
      setLoading(false);
    }
  }

  function download() {
    if (!result) return;
    const binary = atob(result.file.base64);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    const url = URL.createObjectURL(new Blob([bytes], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = result.file.filename;
    link.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="grid gap-6 lg:grid-cols-[380px_1fr]">
      <form onSubmit={handleSubmit} className="space-y-5 rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <div>
          <label className="block text-sm font-semibold text-slate-800">Export FNE</label>
          <input
            ref={inputRef}
            type="file"
            accept=".csv,.txt,.xlsx,.xlsm,.json"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
            className="mt-2 block w-full cursor-pointer rounded-lg border border-dashed border-slate-300 p-3 text-sm file:mr-3 file:rounded-md file:border-0 file:bg-brand-500 file:px-3 file:py-1.5 file:text-white"
          />
          <p className="mt-1 text-xs text-slate-500">CSV, Excel (.xlsx) ou JSON, 20 Mo maximum.</p>
        </div>

        <div>
          <label className="block text-sm font-semibold text-slate-800">Format d&apos;import Sage</label>
          <select
            value={profileId}
            onChange={(event) => setProfileId(event.target.value)}
            className="mt-2 w-full rounded-lg border border-slate-300 bg-white p-2 text-sm"
          >
            {profiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.label}
              </option>
            ))}
          </select>
          {selectedProfile && <p className="mt-1 text-xs text-slate-500">{selectedProfile.description}</p>}
        </div>

        <div>
          <label className="block text-sm font-semibold text-slate-800">
            Correspondance clients (NCC ou nom &rarr; compte Sage)
          </label>
          <textarea
            value={customers}
            onChange={(event) => setCustomers(event.target.value)}
            rows={5}
            placeholder={"1234567 A;ETS KOUAME ET FILS;411KOUAME\n7654321 B;;411SID"}
            className="mt-2 w-full rounded-lg border border-slate-300 p-2 font-mono text-xs"
          />
          <p className="mt-1 text-xs text-slate-500">
            Une ligne par client : <code>ncc;nom;compte Sage</code>. Le NCC ou le nom suffit.
          </p>
        </div>

        <div>
          <label className="block text-sm font-semibold text-slate-800">Compte tiers par defaut</label>
          <input
            value={compteParDefaut}
            onChange={(event) => setCompteParDefaut(event.target.value)}
            placeholder="411DIVERS"
            className="mt-2 w-full rounded-lg border border-slate-300 p-2 text-sm"
          />
          <p className="mt-1 text-xs text-slate-500">
            Utilise pour les clients absents de la table de correspondance.
          </p>
        </div>

        <div>
          <label className="block text-sm font-semibold text-slate-800">
            Correspondance modes de reglement
          </label>
          <textarea
            value={reglements}
            onChange={(event) => setReglements(event.target.value)}
            rows={3}
            placeholder={"deferred=CRED\ncash=ESP\ntransfer=VIR"}
            className="mt-2 w-full rounded-lg border border-slate-300 p-2 font-mono text-xs"
          />
          <p className="mt-1 text-xs text-slate-500">
            Codes FNE : cash, card, check, mobile-money, transfer, deferred.
          </p>
        </div>

        <div>
          <label className="block text-sm font-semibold text-slate-800">Numero de piece Sage</label>
          <select
            value={numeroPiece}
            onChange={(event) => setNumeroPiece(event.target.value)}
            className="mt-2 w-full rounded-lg border border-slate-300 bg-white p-2 text-sm"
          >
            <option value="sequence">Annee + numero (26000000889) - 13 caracteres max</option>
            <option value="reference">Reference FNE complete (2304903U26000000889)</option>
          </select>
          <p className="mt-1 text-xs text-slate-500">
            La reference complete depasse la longueur du champ Sage : elle reste toujours ecrite dans
            la zone &laquo; Reference FNE &raquo;.
          </p>
        </div>

        <button
          type="submit"
          disabled={loading}
          className="w-full rounded-lg bg-brand-600 px-4 py-2.5 font-semibold text-white transition hover:bg-brand-700 disabled:opacity-50"
        >
          {loading ? "Conversion en cours..." : "Convertir"}
        </button>

        {error && (
          <p className="rounded-lg bg-red-50 p-3 text-sm text-red-700" role="alert">
            {error}
          </p>
        )}
      </form>

      <section className="space-y-5">
        {!result && (
          <div className="rounded-xl border border-dashed border-slate-300 bg-white p-10 text-center text-slate-500">
            Le resultat de la conversion s&apos;affichera ici : totaux de controle, anomalies
            detectees et apercu du fichier a importer dans Sage.
          </div>
        )}
        {result && <Results result={result} onDownload={download} />}
      </section>
    </div>
  );
}

function Results({ result, onDownload }: { result: ConvertResult; onDownload: () => void }) {
  const erreurs = result.issues.filter((issue) => issue.severity === "erreur");
  const avertissements = result.issues.filter((issue) => issue.severity === "avertissement");

  const mappedFields = useMemo(
    () => Object.entries(result.mapping) as Array<[FneField, string]>,
    [result.mapping],
  );
  const totaux = money.format(result.summary.totalTTC);

  return (
    <>
      {result.source.synthese && (
        <div className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">
          <strong>Export sans detail des articles.</strong> Une ligne de synthese a ete generee par
          facture a partir des totaux. Les factures melangeant plusieurs taux de TVA ne peuvent pas
          etre reconstituees : utilisez l&apos;export <strong>JSON</strong> de FNE, seul format qui
          porte le detail des articles.
        </div>
      )}

      <div className="grid gap-3 sm:grid-cols-4">
        <Stat label="Factures" value={String(result.summary.factures)} />
        <Stat label="Avoirs" value={String(result.summary.avoirs)} />
        <Stat label="Lignes" value={String(result.summary.lignes)} />
        <Stat label="Total TTC" value={`${totaux} XOF`} />
      </div>

      <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="font-semibold text-slate-900">Fichier d&apos;import</h2>
            <p className="text-sm text-slate-600">
              {result.file.filename} &middot; {result.file.lineCount} enregistrements &middot;{" "}
              {result.profile.label}
            </p>
          </div>
          <button
            onClick={onDownload}
            disabled={erreurs.length > 0}
            title={erreurs.length > 0 ? "Corrigez les erreurs bloquantes avant de telecharger." : undefined}
            className="rounded-lg bg-brand-600 px-4 py-2 font-semibold text-white transition hover:bg-brand-700 disabled:opacity-40"
          >
            Telecharger
          </button>
        </div>
        <pre className="mt-4 max-h-64 overflow-auto rounded-lg bg-slate-900 p-3 font-mono text-xs text-slate-100">
          {result.file.content.slice(0, 4000) || "(fichier vide)"}
        </pre>
      </div>

      {erreurs.length > 0 && (
        <IssueList title={`${erreurs.length} erreur(s) bloquante(s)`} tone="error" issues={erreurs} />
      )}
      {avertissements.length > 0 && (
        <IssueList
          title={`${avertissements.length} avertissement(s)`}
          tone="warning"
          issues={avertissements}
        />
      )}

      {result.clientsInconnus.length > 0 && (
        <div className="rounded-xl border border-amber-200 bg-amber-50 p-5">
          <h2 className="font-semibold text-amber-900">
            {result.clientsInconnus.length} client(s) sans compte tiers Sage
          </h2>
          <table className="mt-3 w-full text-sm">
            <thead className="text-left text-amber-900/70">
              <tr>
                <th className="py-1">Client</th>
                <th className="py-1">NCC</th>
                <th className="py-1">Factures</th>
              </tr>
            </thead>
            <tbody>
              {result.clientsInconnus.map((client) => (
                <tr key={`${client.ncc}-${client.nom}`} className="border-t border-amber-200/60">
                  <td className="py-1">{client.nom || "-"}</td>
                  <td className="py-1 font-mono text-xs">{client.ncc || "-"}</td>
                  <td className="py-1 text-xs">{client.factures.join(", ")}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 className="font-semibold text-slate-900">
          {result.source.kind === "fne-json" ? "Source" : "Colonnes reconnues"}
        </h2>
        <p className="text-sm text-slate-600">
          {result.source.kind === "fne-json"
            ? "Export JSON natif FNE : les articles sont lus directement, sans mappage de colonnes."
            : `Tableau ${result.source.format.toUpperCase()}${
                result.source.sheet ? ` (feuille ${result.source.sheet})` : ""
              }`}{" "}
          &middot; {result.source.rowCount} enregistrements lus.
        </p>
        <ul className="mt-3 grid gap-1 text-sm sm:grid-cols-2">
          {mappedFields.map(([field, column]) => (
            <li key={field} className="flex justify-between gap-2 border-b border-slate-100 py-1">
              <span className="text-slate-600">{FIELD_LABELS[field]}</span>
              <span className="font-mono text-xs text-slate-900">{column}</span>
            </li>
          ))}
        </ul>
        {result.unmappedColumns.length > 0 && (
          <p className="mt-3 text-sm text-amber-700">
            Colonnes non reconnues : {result.unmappedColumns.join(", ")}
          </p>
        )}
        {result.ignoredColumns.length > 0 && (
          <p className="mt-2 text-sm text-slate-500">
            Colonnes ignorees (sans usage cote Sage) : {result.ignoredColumns.join(", ")}
          </p>
        )}
      </div>
    </>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <p className="text-xs uppercase tracking-wide text-slate-500">{label}</p>
      <p className="mt-1 text-xl font-semibold text-slate-900">{value}</p>
    </div>
  );
}

function IssueList({
  title,
  tone,
  issues,
}: {
  title: string;
  tone: "error" | "warning";
  issues: ConvertResult["issues"];
}) {
  const styles =
    tone === "error"
      ? "border-red-200 bg-red-50 text-red-900"
      : "border-slate-200 bg-white text-slate-800";
  return (
    <div className={`rounded-xl border p-5 shadow-sm ${styles}`}>
      <h2 className="font-semibold">{title}</h2>
      <ul className="mt-2 space-y-1 text-sm">
        {issues.slice(0, 100).map((issue, index) => (
          <li key={`${issue.code}-${index}`} className="flex gap-2">
            <span className="font-mono text-xs opacity-60">{issue.facture ?? "-"}</span>
            <span>{issue.message}</span>
          </li>
        ))}
      </ul>
      {issues.length > 100 && <p className="mt-2 text-xs opacity-70">... et {issues.length - 100} autres.</p>}
    </div>
  );
}
