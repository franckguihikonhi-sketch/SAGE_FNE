import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "SAGE FNE - Convertisseur de factures",
  description:
    "Transforme les exports de factures FNE (DGI Cote d'Ivoire) en fichier d'import Sage 100 Gestion Commerciale.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="fr">
      <body className="min-h-screen antialiased">{children}</body>
    </html>
  );
}
