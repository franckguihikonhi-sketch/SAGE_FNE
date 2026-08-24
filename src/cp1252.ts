/**
 * Encodage Windows-1252, celui attendu par Sage 100.
 *
 * Ecrit a la main plutot que via iconv-lite : le meme code sert au serveur et
 * au navigateur, ou les modules Node ne sont pas disponibles.
 */

/**
 * Plage 0x80-0x9F, seule difference avec Latin-1, en points de code Unicode.
 * `null` marque les cinq positions non definies par Windows-1252.
 */
const HIGH: Array<number | null> = [
  0x20ac, null, 0x201a, 0x0192, 0x201e, 0x2026, 0x2020, 0x2021,
  0x02c6, 0x2030, 0x0160, 0x2039, 0x0152, null, 0x017d, null,
  null, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014,
  0x02dc, 0x2122, 0x0161, 0x203a, 0x0153, null, 0x017e, 0x0178,
];

const TO_BYTE = new Map<number, number>();
HIGH.forEach((codePoint, index) => {
  if (codePoint !== null) TO_BYTE.set(codePoint, 0x80 + index);
});

/** Les caracteres hors du jeu Windows-1252 sont remplaces plutot que perdus. */
export function encodeCp1252(text: string, remplacement = 0x3f): Uint8Array {
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i += 1) {
    const code = text.charCodeAt(i);
    if (code <= 0x7f || (code >= 0xa0 && code <= 0xff)) {
      bytes[i] = code;
      continue;
    }
    bytes[i] = TO_BYTE.get(code) ?? remplacement;
  }
  return bytes;
}

/** Encodage base64 d'un flux d'octets, sans dependre de Buffer. */
export function toBase64(bytes: Uint8Array): string {
  let binaire = "";
  for (const byte of bytes) binaire += String.fromCharCode(byte);
  // btoa cote navigateur, Buffer cote Node : les deux attendent du binaire brut.
  if (typeof btoa === "function") return btoa(binaire);
  return Buffer.from(bytes).toString("base64");
}

/**
 * Decode un fichier texte sans connaitre son encodage : UTF-8 par defaut,
 * Windows-1252 en repli, encodage usuel des exports Excel francophones.
 */
export function decodeText(bytes: Uint8Array): string {
  const sansBom =
    bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf
      ? bytes.subarray(3)
      : bytes;
  const utf8 = new TextDecoder("utf-8").decode(sansBom);
  // U+FFFD signale un decodage UTF-8 rate.
  return utf8.includes("\uFFFD") ? decodeCp1252(sansBom) : utf8;
}

export function decodeCp1252(bytes: Uint8Array): string {
  let out = "";
  for (const byte of bytes) {
    if (byte >= 0x80 && byte <= 0x9f) {
      const codePoint = HIGH[byte - 0x80];
      out += codePoint === null ? "�" : String.fromCharCode(codePoint!);
      continue;
    }
    out += String.fromCharCode(byte);
  }
  return out;
}
