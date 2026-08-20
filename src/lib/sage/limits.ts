/**
 * Longueurs des zones Sage 100 Gestion Commerciale.
 *
 * A CONFIRMER sur le dossier du client : ces valeurs correspondent aux
 * longueurs standard des champs de la base Sage 100. Elles servent a prevenir
 * les rejets d'import lies a une troncature.
 */
export const SAGE_LIMITS = {
  /** DO_Piece : numero de piece du document. */
  numeroPiece: 13,
  /** CT_Num : numero de compte tiers. */
  compteTiers: 17,
  /** AR_Ref : reference article. */
  referenceArticle: 19,
  /** DL_Design : designation de la ligne. */
  designation: 69,
  /** DO_Ref : reference du document. */
  reference: 17,
} as const;
