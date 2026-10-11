import type { PokerCategoryName, PokerEvent, PokerShowdownEntry } from "../api/types";
import { formatChips } from "./format";

// Poker en el navegador. Las reglas las decide el servidor: aca solo se DECODIFICA cada carta (0 a 51) para dibujarla y se redactan los hechos
// de la mano en español. Mismo reparto de ids que PokerCards del servidor: palo = id / 13, rango = id % 13 + 2.

export const DECK_SIZE = 52;
export type PokerSuit = 0 | 1 | 2 | 3;

export const SUIT_SYMBOLS = ["♠", "♥", "♦", "♣"] as const;
export const SUIT_NAMES = ["picas", "corazones", "diamantes", "tréboles"] as const;
const RANK_SYMBOLS: Record<number, string> = { 11: "J", 12: "Q", 13: "K", 14: "A" };
const RANK_NAMES: Record<number, string> = { 11: "Jota", 12: "Reina", 13: "Rey", 14: "As" };

export interface PokerCardInfo {
  suit: PokerSuit;
  /** 2 a 14 (J=11, Q=12, K=13, A=14). */
  rank: number;
  /** "A", "10", "7". */
  rankSymbol: string;
  symbol: string;
  /** Corazones y diamantes. */
  red: boolean;
  /** Para lectores de pantalla: "As de picas", "10 de corazones". */
  label: string;
}

export const isValidCard = (card: number): boolean => Number.isInteger(card) && card >= 0 && card < DECK_SIZE;

export function decodeCard(card: number): PokerCardInfo {
  if (!isValidCard(card)) throw new RangeError(`Carta de poker fuera de rango: ${card}`);
  const suit = Math.floor(card / 13) as PokerSuit;
  const rank = (card % 13) + 2;
  return {
    suit,
    rank,
    rankSymbol: RANK_SYMBOLS[rank] ?? String(rank),
    symbol: SUIT_SYMBOLS[suit],
    red: suit === 1 || suit === 2,
    label: `${RANK_NAMES[rank] ?? rank} de ${SUIT_NAMES[suit]}`,
  };
}

const CATEGORY_NAMES: Record<PokerCategoryName, string> = {
  HighCard: "Carta alta",
  Pair: "Pareja",
  TwoPair: "Doble pareja",
  ThreeOfAKind: "Trío",
  Straight: "Escalera",
  Flush: "Color",
  FullHouse: "Full",
  FourOfAKind: "Póker",
  StraightFlush: "Escalera de color",
};

/** Nombre en español de la categoria. Con las cartas a mano, la escalera de color con As arriba es la "Escalera real". */
export function categoryName(category: PokerCategoryName | string, cards: readonly number[] = []): string {
  if (category === "StraightFlush") {
    const ranksBySuit = new Map<number, Set<number>>();
    for (const c of cards) {
      if (!isValidCard(c)) continue;
      const info = decodeCard(c);
      ranksBySuit.set(info.suit, (ranksBySuit.get(info.suit) ?? new Set<number>()).add(info.rank));
    }
    for (const ranks of ranksBySuit.values()) if ([10, 11, 12, 13, 14].every((r) => ranks.has(r))) return "Escalera real";
  }
  return CATEGORY_NAMES[category as PokerCategoryName] ?? category;
}

/** La mano de un asiento en el showdown con su nombre (la escalera real se detecta con sus 2 cartas y las comunitarias). */
export const handName = (entry: PokerShowdownEntry, board: readonly number[]): string => categoryName(entry.category, [...entry.cards, ...board]);

export const cardsLabel = (cards: readonly number[]): string => cards.map((c) => decodeCard(c).label).join(", ");

export const chipsText = (n: number): string => `${formatChips(n)} ${n === 1 ? "ficha" : "fichas"}`;

/**
 * El hecho de la mano en una frase: "Jugador 2 subió a 40", "Bot 3 igualó 20", "Jugador 1 pasó", "Se retiró Bot 2", "Flop: 7 de picas, ...".
 * `nameOf` da el nombre del asiento (viene del servidor: "Jugador 2" / "Bot 3"). `board` son las comunitarias (para nombrar las cartas de cada
 * calle) y `showdown` da el nombre de la mano de quien gana en el showdown.
 */
export function eventText(event: PokerEvent, nameOf: (seat: number) => string, board: readonly number[] = [], showdown: readonly PokerShowdownEntry[] = []): string {
  const who = event.seat >= 0 ? nameOf(event.seat) : "";
  const amount = formatChips(event.amount ?? 0);
  const allIn = event.allIn ? " (all-in)" : "";
  switch (event.kind) {
    case "blind":
      return `${who} puso la ciega de ${amount}${allIn}`;
    case "fold":
      return `Se retiró ${who}`;
    case "check":
      return `${who} pasó`;
    case "call":
      return `${who} igualó ${amount}${allIn}`;
    case "bet":
      return `${who} apostó ${amount}${allIn}`;
    case "raise":
      return `${who} subió a ${amount}${allIn}`;
    case "flop":
      return board.length >= 3 ? `Flop: ${cardsLabel(board.slice(0, 3))}` : "Flop";
    case "turn":
      return board.length >= 4 ? `Turn: ${decodeCard(board[3]!).label}` : "Turn";
    case "river":
      return board.length >= 5 ? `River: ${decodeCard(board[4]!).label}` : "River";
    case "win":
      return `${who} se llevó ${amount}`;
    case "win_showdown": {
      const entry = showdown.find((s) => s.seat === event.seat);
      return entry ? `${who} se llevó ${amount} con ${handName(entry, board)}` : `${who} se llevó ${amount}`;
    }
  }
}

/** Mantiene un monto de subida entero y dentro de [min, max] (si el maximo es menor que el minimo, gana el maximo: es all-in). */
export function clampRaise(value: number, minRaiseTo: number, maxRaiseTo: number): number {
  const lo = Math.min(minRaiseTo, maxRaiseTo);
  if (!Number.isFinite(value)) return lo;
  return Math.min(Math.max(Math.trunc(value), lo), maxRaiseTo);
}

export type RaiseSize = "half" | "pot" | "allin";

/**
 * Atajos de subida ("a cuanto", total en esta ronda): lo que ya tengo puesto (`myBet`) mas `toCall` mas la mitad del pozo o el pozo entero
 * (contando lo que voy a igualar). Siempre dentro de [minRaiseTo, maxRaiseTo].
 */
export function raiseSize(size: RaiseSize, o: { pot: number; myBet: number; toCall: number; minRaiseTo: number; maxRaiseTo: number }): number {
  if (size === "allin") return o.maxRaiseTo;
  const potAfterCall = o.pot + o.toCall;
  const extra = size === "half" ? Math.floor(potAfterCall / 2) : potAfterCall;
  return clampRaise(o.myBet + o.toCall + extra, o.minRaiseTo, o.maxRaiseTo);
}
