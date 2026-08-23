import { NextRequest, NextResponse } from "next/server";
import { convertFichier as convert } from "@/lib/node/convert";
import { parseArticlesTauxText } from "@/lib/sage/articles";
import { parseCustomerMappingCsv } from "@/lib/sage/customers";
import { parsePaymentMappingText } from "@/lib/fne/paiement";
import { ReadError } from "@/lib/fne/read";

export const runtime = "nodejs";
export const maxDuration = 60;

const MAX_SIZE = 20 * 1024 * 1024;

export async function POST(request: NextRequest) {
  let form: FormData;
  try {
    form = await request.formData();
  } catch {
    return NextResponse.json({ error: "Requete invalide." }, { status: 400 });
  }

  const file = form.get("file");
  if (!(file instanceof File)) {
    return NextResponse.json({ error: "Aucun fichier recu." }, { status: 400 });
  }
  if (file.size === 0) {
    return NextResponse.json({ error: "Le fichier est vide." }, { status: 400 });
  }
  if (file.size > MAX_SIZE) {
    return NextResponse.json(
      { error: `Fichier trop volumineux (${Math.round(file.size / 1024 / 1024)} Mo, maximum 20 Mo).` },
      { status: 413 },
    );
  }

  const buffer = Buffer.from(await file.arrayBuffer());
  const profileId = asString(form.get("profileId"));
  const customersCsv = asString(form.get("customers"));
  const compteParDefaut = asString(form.get("compteParDefaut"));
  const exigerCompteTiers = asString(form.get("exigerCompteTiers")) !== "false";
  const reglements = asString(form.get("reglements"));
  const numeroSaisi = asString(form.get("numeroPiece"));
  const numeroPiece =
    numeroSaisi === "reference" || numeroSaisi === "vide" ? numeroSaisi : "sequence";
  const parametres = {
    depot: asString(form.get("depot")),
    souche: asString(form.get("souche")) || "1",
  };

  try {
    const result = await convert(buffer, file.name, {
      profileId: profileId || undefined,
      customers: customersCsv ? parseCustomerMappingCsv(customersCsv) : [],
      customerOptions: { compteParDefaut: compteParDefaut || undefined, utiliserCodeSource: true },
      reglements: reglements ? parsePaymentMappingText(reglements) : {},
      parametres,
      normalizeOptions: {
        numeroPiece,
        articlesSynthese: parseArticlesTauxText(asString(form.get("articlesTaux"))),
      },
      validationOptions: { exigerCompteTiers },
    });
    return NextResponse.json(result);
  } catch (error) {
    if (error instanceof ReadError) {
      return NextResponse.json({ error: error.message }, { status: 422 });
    }
    const message = error instanceof Error ? error.message : "Erreur inattendue pendant la conversion.";
    return NextResponse.json({ error: message }, { status: 500 });
  }
}

function asString(value: FormDataEntryValue | null): string {
  return typeof value === "string" ? value.trim() : "";
}
