import type { BlackjackHand, BlackjackResult, BlackjackRound, BlackjackSeat } from "../api/types";
import { sha256Hex } from "./crash";

// Las reglas y el mazo de Blackjack en el navegador. Las reglas de pago las decide el servidor: aca solo se CUENTAN los puntos para mostrarlos
// y se VERIFICA una mano (recalcular el zapato con la semilla revelada). Es el MISMO algoritmo que BlackjackMath.Shoe del servidor.

export const DECKS = 6;
export const SHOE_SIZE = DECKS * 52;

const RANK_NAMES = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K"];
const SUITS = [
  { symbol: "♠", name: "picas", red: false },
  { symbol: "♥", name: "corazones", red: true },
  { symbol: "♦", name: "diamantes", red: true },
  { symbol: "♣", name: "tréboles", red: false },
];

/** 0 = as, 1 a 8 = 2 a 9, 9 a 12 = 10, J, Q, K. */
export const rankOf = (card: number): number => card % 13;
export const suitOf = (card: number): number => Math.floor(card / 13);

export interface CardFace {
  /** "A", "10", "K"... */
  rank: string;
  symbol: string;
  red: boolean;
  /** Para lectores de pantalla: "As de picas". */
  label: string;
}

export function cardFace(card: number): CardFace {
  const suit = SUITS[suitOf(card)]!;
  const rank = rankOf(card);
  const name = RANK_NAMES[rank]!;
  return { rank: name, symbol: suit.symbol, red: suit.red, label: `${rank === 0 ? "As" : name} de ${suit.name}` };
}

export interface HandValue {
  total: number;
  /** Cuenta un as como 11. */
  soft: boolean;
}

/** El as vale 11 y baja a 1 mientras el total se pase de 21. */
export function handValue(cards: readonly number[]): HandValue {
  let total = 0;
  let aces = 0;
  for (const card of cards) {
    const rank = rankOf(card);
    total += rank === 0 ? 11 : rank >= 9 ? 10 : rank + 1;
    if (rank === 0) aces += 1;
  }
  while (total > 21 && aces > 0) {
    total -= 10;
    aces -= 1;
  }
  return { total, soft: aces > 0 };
}

export const isBlackjack = (cards: readonly number[]): boolean => cards.length === 2 && handValue(cards).total === 21;

/** "17", "17 blando" o "Blackjack". */
export function describeHand(cards: readonly number[]): string {
  if (cards.length === 0) return "";
  if (isBlackjack(cards)) return "Blackjack";
  const { total, soft } = handValue(cards);
  return soft && total < 21 ? `${total} blando` : `${total}`;
}

const RESULT_TEXT: Record<BlackjackResult, string> = {
  Blackjack: "¡Blackjack! Paga 3 a 2",
  Win: "Ganaste: paga 1 a 1",
  Push: "Empate: te devuelven la apuesta",
  Lose: "Perdiste: el crupier te ganó",
  Bust: "Te pasaste",
};

export const resultText = (result: BlackjackResult): string => RESULT_TEXT[result];

const HAND_TEXT: Record<BlackjackHand, string> = {
  Waiting: "Esperando el reparto",
  Playing: "Jugando",
  Stood: "Plantado",
  Bust: "Se pasó",
  Blackjack: "Blackjack",
};

export const handText = (hand: BlackjackHand): string => HAND_TEXT[hand];

/** Una mano que gano fichas (blackjack o ganada): se festeja. Un empate devuelve lo apostado y no cuenta. */
export const isWin = (result: BlackjackResult | null): boolean => result === "Blackjack" || result === "Win";

// ---- Verificacion provably fair ----

const encoder = new TextEncoder();

/** Los numeros del zapato: bloques HMAC-SHA256(semilla, "blackjack:{id sin guiones}:{k}") de 8 palabras de 32 bits (big endian). */
async function* wordStream(serverSeed: string, roundId: string): AsyncGenerator<number> {
  const key = await crypto.subtle.importKey("raw", encoder.encode(serverSeed), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const id = roundId.replaceAll("-", "").toLowerCase();
  for (let k = 0; ; k += 1) {
    const block = new DataView(await crypto.subtle.sign("HMAC", key, encoder.encode(`blackjack:${id}:${k}`)));
    for (let w = 0; w < 8; w += 1) yield block.getUint32(w * 4, false);
  }
}

/** El zapato barajado de una mano: 312 cartas (0 a 51, seis veces) en el orden en que se reparten. Fisher-Yates con rechazo para no sesgar. */
export async function shoeOf(serverSeed: string, roundId: string): Promise<number[]> {
  const order = Array.from({ length: SHOE_SIZE }, (_, i) => i);
  const words = wordStream(serverSeed, roundId);
  const below = async (n: number): Promise<number> => {
    const limit = 2 ** 32 - (2 ** 32 % n);
    for (;;) {
      const x = (await words.next()).value as number;
      if (x < limit) return x % n;
    }
  };
  for (let i = SHOE_SIZE - 1; i >= 1; i -= 1) {
    const j = await below(i + 1);
    [order[i], order[j]] = [order[j]!, order[i]!];
  }
  void words.return(undefined);
  return order.map((n) => n % 52);
}

export interface HandVerification {
  /** La semilla revelada coincide con el compromiso publicado antes de repartir. */
  commitmentOk: boolean;
  /** Las cartas iniciales de los asientos y las del crupier salen del zapato recalculado. */
  dealtOk: boolean;
}

// Una carta que todavia no existe (por ejemplo la tapada) no se compara.
const sameCard = (shown: number | undefined, expected: number | undefined) => shown === undefined || shown === expected;

/**
 * Verifica una mano terminada. Reparto con N asientos: carta inicial del asiento s (0 a N-1) = zapato[s]; carta visible del crupier = zapato[N];
 * segunda del asiento s = zapato[N+1+s]; la tapada = zapato[2N+1]. Los asientos del servidor se numeran desde 1. Devuelve null si todavia no se revelo la semilla.
 */
export async function verifyHand(round: BlackjackRound, seats: readonly BlackjackSeat[]): Promise<HandVerification | null> {
  if (round.serverSeed === null) return null;
  const shoe = await shoeOf(round.serverSeed, round.id);
  const n = round.seatCount;

  const seatsOk = seats.every((seat) => {
    if (seat.seat === null) return true;
    const s = seat.seat - 1;
    return s >= 0 && s < n && sameCard(seat.cards[0], shoe[s]) && sameCard(seat.cards[1], shoe[n + 1 + s]);
  });
  const dealerOk = sameCard(round.dealer.cards[0], shoe[n]) && sameCard(round.dealer.cards[1], shoe[2 * n + 1]);

  return { commitmentOk: (await sha256Hex(round.serverSeed)) === round.commitment.toLowerCase(), dealtOk: seatsOk && dealerOk };
}
