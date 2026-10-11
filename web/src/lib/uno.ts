import type { UnoEvent } from "../api/types";

// Las cartas de Uno en el navegador. Las reglas las decide el servidor: aca solo se DECODIFICA cada carta (0 a 107) para dibujarla y se redactan los
// hechos de la partida en español. Mismo reparto de ids que UnoCards del servidor.

export type UnoColor = 0 | 1 | 2 | 3;
export type UnoKind = "number" | "skip" | "reverse" | "draw2" | "wild" | "wild4";

/** Rojo, amarillo, verde y azul, en el orden de los ids. */
export const COLOR_NAMES = ["rojo", "amarillo", "verde", "azul"] as const;
/** Colores de las cartas (el amarillo lleva texto oscuro: ver `textOn`). */
export const COLOR_CSS = ["#d63a3a", "#f2c230", "#2fa968", "#2f6fd6"] as const;
export const COLORS: readonly UnoColor[] = [0, 1, 2, 3];

export const WILD_FIRST = 100;
export const WILD4_FIRST = 104;
export const DECK_SIZE = 108;

export interface UnoCardInfo {
  kind: UnoKind;
  /** null en los comodines. */
  color: UnoColor | null;
  /** Solo en las de numero (0 a 9). */
  number: number | null;
  /** Lo que se dibuja en la carta: "5", "⊘", "⇄", "+2", "★" o "+4". */
  symbol: string;
  /** Para lectores de pantalla y botones: "5 rojo", "+2 azul", "Salto verde", "Comodín +4". */
  label: string;
}

export const isValidCard = (card: number): boolean => Number.isInteger(card) && card >= 0 && card < DECK_SIZE;
export const isWild = (card: number): boolean => card >= WILD_FIRST;

export function decodeCard(card: number): UnoCardInfo {
  if (!isValidCard(card)) throw new RangeError(`Carta de Uno fuera de rango: ${card}`);
  if (card >= WILD4_FIRST) return { kind: "wild4", color: null, number: null, symbol: "+4", label: "Comodín +4" };
  if (card >= WILD_FIRST) return { kind: "wild", color: null, number: null, symbol: "★", label: "Comodín" };
  const color = Math.floor(card / 25) as UnoColor;
  const k = card % 25;
  const name = COLOR_NAMES[color];
  if (k <= 18) {
    const n = k === 0 ? 0 : k <= 9 ? k : k - 9;
    return { kind: "number", color, number: n, symbol: String(n), label: `${n} ${name}` };
  }
  if (k <= 20) return { kind: "skip", color, number: null, symbol: "⊘", label: `Salto ${name}` };
  if (k <= 22) return { kind: "reverse", color, number: null, symbol: "⇄", label: `Reversa ${name}` };
  return { kind: "draw2", color, number: null, symbol: "+2", label: `+2 ${name}` };
}

/** Color de la carta (null en los comodines). */
export const colorOf = (card: number): UnoColor | null => decodeCard(card).color;

/** Texto legible sobre el color de la carta. */
export const textOn = (color: number | null): string => (color === 1 ? "#1b1b1b" : "#ffffff");

export const colorName = (color: number): string => COLOR_NAMES[color] ?? "";

/** La carta con articulo: "un 5 rojo", "un comodín +4". */
const withArticle = (card: number): string => {
  const info = decodeCard(card);
  const label = info.kind === "wild" || info.kind === "wild4" ? info.label.toLowerCase() : info.label;
  return `un ${label}`;
};

const cards = (n: number): string => (n === 1 ? "una carta" : `${n} cartas`);

/**
 * El hecho de la partida en una frase: "Jugador 2 jugó un +2 azul", "Bot 3 robó una carta", "Jugador 2 se salteó",
 * "Jugador 4 levantó 2 cartas". `nameOf` da el nombre del asiento (viene del servidor: "Jugador 2" / "Bot 3").
 */
export function eventText(event: UnoEvent, nameOf: (seat: number) => string): string {
  const who = nameOf(event.seat);
  switch (event.kind) {
    case "start":
      return event.card === null ? "Empezó la partida" : `Empezó la partida con ${withArticle(event.card)}`;
    case "play": {
      if (event.card === null) return `${who} jugó una carta`;
      const chosen = isWild(event.card) && event.color !== null ? ` y eligió ${colorName(event.color)}` : "";
      return `${who} jugó ${withArticle(event.card)}${chosen}`;
    }
    case "draw":
      return event.count === 0 ? `${who} quiso robar pero no quedan cartas` : `${who} robó ${cards(event.count ?? 1)}`;
    case "pass":
      return `${who} pasó`;
    case "skipped":
      return `${who} se salteó`;
    case "penalty":
      return `${who} levantó ${cards(event.count ?? 1)}`;
  }
}

/** El cuerpo de la jugada: los comodines llevan el color elegido. */
export function playBody(card: number, color?: number): { type: "play"; card: number; color?: number } {
  return isWild(card) ? { type: "play", card, color: color ?? 0 } : { type: "play", card };
}
