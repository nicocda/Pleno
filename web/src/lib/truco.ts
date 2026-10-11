import type { TrucoAction, TrucoActionType, TrucoEvent, TrucoPending } from "../api/types";

// Las cartas del Truco en el navegador. Las reglas las decide el servidor: aca solo se DECODIFICA cada carta (0 a 39, baraja española) para dibujarla,
// se redactan los hechos de la partida en español y se rotulan los botones de cada jugada. Mismo reparto de ids que TrucoCards del servidor.

export const DECK_SIZE = 40;
const NUMBERS = [1, 2, 3, 4, 5, 6, 7, 10, 11, 12] as const;

export type TrucoSuit = 0 | 1 | 2 | 3;
export const SUIT_NAMES = ["espadas", "bastos", "oros", "copas"] as const;
/** Simbolos simples de cada palo (texto, sin imagenes). */
export const SUIT_SYMBOLS = ["♠", "♣", "●", "♥"] as const;
/** Color de cada palo sobre la carta clara. */
export const SUIT_CSS = ["#1f4fa8", "#1f7a46", "#b8860b", "#c02b2b"] as const;

export interface TrucoCardInfo {
  suit: TrucoSuit;
  number: number;
  symbol: string;
  /** Para lectores de pantalla y botones: "1 de espadas". */
  label: string;
}

export const isValidCard = (card: number): boolean => Number.isInteger(card) && card >= 0 && card < DECK_SIZE;

export function decodeCard(card: number): TrucoCardInfo {
  if (!isValidCard(card)) throw new RangeError(`Carta de Truco fuera de rango: ${card}`);
  const suit = Math.floor(card / 10) as TrucoSuit;
  const number = NUMBERS[card % 10]!;
  return { suit, number, symbol: SUIT_SYMBOLS[suit], label: `${number} de ${SUIT_NAMES[suit]}` };
}

/** Cuanto vale la carta para ganar una mano (mas alto gana): 14 el 1 de espadas ... 1 los 4. Sirve para ordenar la mano. */
export function strengthOf(card: number): number {
  const { suit, number } = decodeCard(card);
  switch (number) {
    case 1: return suit === 0 ? 14 : suit === 1 ? 13 : 8;
    case 7: return suit === 0 ? 12 : suit === 2 ? 11 : 4;
    case 3: return 10;
    case 2: return 9;
    case 12: return 7;
    case 11: return 6;
    case 10: return 5;
    case 6: return 3;
    case 5: return 2;
    default: return 1;
  }
}

/** Mejor carta primero. */
export const byStrength = (a: number, b: number): number => strengthOf(b) - strengthOf(a) || a - b;

const CALL_NAMES: Partial<Record<string, string>> = {
  truco: "truco",
  retruco: "retruco",
  vale4: "vale cuatro",
  envido: "envido",
  real_envido: "real envido",
  falta_envido: "falta envido",
};

const signed = (n: number): string => `+${n}`;

/**
 * El hecho de la partida en una frase: "Jugador 2 cantó truco", "Bot 2 no quiso", "Ganó la mano Jugador 1", "Parda", "Ronda para Jugador 2: +2".
 * `nameOf` da el nombre del asiento (viene del servidor: "Jugador 2" / "Bot 2").
 */
export function eventText(event: TrucoEvent, nameOf: (seat: number) => string): string {
  const who = nameOf(event.seat);
  switch (event.kind) {
    case "start":
      return "Empezó la ronda";
    case "play":
      return event.card === null ? `${who} jugó una carta` : `${who} jugó ${decodeCard(event.card).label}`;
    case "truco":
    case "retruco":
    case "vale4":
    case "envido":
    case "real_envido":
    case "falta_envido":
      return `${who} cantó ${CALL_NAMES[event.kind]}`;
    case "quiero":
      return `${who} quiso`;
    case "no_quiero":
      return `${who} no quiso`;
    case "mazo":
      return `${who} se fue al mazo`;
    case "baza":
      return `Ganó la mano ${who}`;
    case "parda":
      return "Parda";
    case "envido_result": {
      const points = event.value === null ? "" : ` (${signed(event.value)})`;
      if (event.a === null || event.b === null) return `Envido: ganó ${who}${points}`;
      return `Envido: ${nameOf(0)} tenía ${event.a} y ${nameOf(1)} ${event.b} — ganó ${who}${points}`;
    }
    case "hand_end":
      return `Ronda para ${who}: ${signed(event.value ?? 0)}`;
  }
}

/** Lo que valdria la ronda en cada nivel de truco vigente. */
export const trucoWorth = (level: number): number => (level <= 0 ? 1 : level + 1);

export const trucoLevelName = (level: number): string => (level === 1 ? "Truco" : level === 2 ? "Retruco" : level === 3 ? "Vale cuatro" : "Sin truco");

const PLAIN: Record<Exclude<TrucoActionType, "play">, string> = {
  envido: "Envido",
  real_envido: "Real envido",
  falta_envido: "Falta envido",
  truco: "Truco",
  retruco: "Retruco",
  vale4: "Vale cuatro",
  quiero: "Quiero",
  no_quiero: "No quiero",
  mazo: "Al mazo",
};

/** El rotulo del boton de una jugada. Con un truco pendiente, subir es "Quiero retruco" / "Quiero vale cuatro"; con un envido pendiente, subir es "Subir: real envido". */
export function actionLabel(type: Exclude<TrucoActionType, "play">, pending: TrucoPending | null): string {
  if (pending?.kind === "truco" && (type === "retruco" || type === "vale4")) return `Quiero ${CALL_NAMES[type]}`;
  if (pending?.kind === "envido" && (type === "envido" || type === "real_envido" || type === "falta_envido")) return `Subir: ${CALL_NAMES[type]}`;
  return PLAIN[type];
}

export type ActionGroup = "envido" | "truco" | "response" | "mazo";

export const GROUP_LABELS: Record<ActionGroup, string> = { envido: "Envido", truco: "Truco", response: "Respuesta", mazo: "Mazo" };

export function groupOf(type: Exclude<TrucoActionType, "play">): ActionGroup {
  switch (type) {
    case "envido":
    case "real_envido":
    case "falta_envido":
      return "envido";
    case "truco":
    case "retruco":
    case "vale4":
      return "truco";
    case "quiero":
    case "no_quiero":
      return "response";
    case "mazo":
      return "mazo";
  }
}

/** El cuerpo de la jugada de una carta. */
export const playBody = (card: number): TrucoAction => ({ type: "play", card });

/** El cartel del canto pendiente: "Jugador 2 cantó truco: ¿quiero?". Con envido, el ultimo canto de la cadena. */
export function pendingText(pending: TrucoPending, nameOf: (seat: number) => string): string {
  const who = nameOf(pending.caller);
  if (pending.kind === "truco") return `${who} cantó ${trucoLevelName(pending.level).toLowerCase()}: ¿quiero?`;
  const chain = pending.calls.map((c) => CALL_NAMES[c] ?? c).join(", ");
  return `${who} cantó ${chain || "envido"}: ¿quiero?`;
}

/** Los nombres de las tres manos de una ronda (en el Truco cada vuelta de cartas se llama "mano"; "es mano" es quien empieza la ronda). */
export const MANO_NAMES = ["Primera mano", "Segunda mano", "Tercera mano"] as const;

export interface PileSpot {
  dx: number;
  dy: number;
  rot: number;
}

/** Cada mano arranca un poco MAS ARRIBA que la anterior (misma columna), asi se lee el apilado de las tres manos. */
const MANO_START = [{ x: -16, y: 22 }, { x: -16, y: 4 }, { x: -16, y: -14 }] as const;
/** La segunda carta de la mano cae al costado y un poco abajo de la primera. */
const SECOND_CARD = { x: 32, y: 18 } as const;
const JITTER = [-7, 5, -3, 8, 4, -6] as const;

/**
 * Donde cae la carta numero `index` (0 = la primera de la ronda) de la pila de la mesa. Las cartas se apilan de a dos por mano: la primera en el punto de partida
 * de la mano y la segunda al costado y un poco abajo; la mano siguiente arranca un poco mas arriba (y su segunda carta otra vez abajo a la derecha), asi se ven las 3 manos.
 */
export function pileSpot(index: number): PileSpot {
  const start = MANO_START[Math.min(Math.floor(index / 2), MANO_START.length - 1)]!;
  const second = index % 2 === 1;
  return {
    dx: start.x + (second ? SECOND_CARD.x : 0),
    dy: start.y + (second ? SECOND_CARD.y : 0),
    rot: JITTER[index % JITTER.length]!,
  };
}

// ---- Carteleria: avisos grandes de lo que va pasando ----

const sameEvent = (a: TrucoEvent, b: TrucoEvent): boolean => a.seat === b.seat && a.kind === b.kind && a.card === b.card && a.value === b.value && a.a === b.a && a.b === b.b;

/**
 * Los hechos que llegaron desde la ultima vez. El servidor manda solo los ultimos hechos (una ventana que se corre), asi que no alcanza con comparar
 * cantidades: se busca cuanto de lo anterior sigue estando al principio de lo nuevo y lo que sobra es lo nuevo.
 */
export function newEvents(previous: readonly TrucoEvent[], next: readonly TrucoEvent[]): TrucoEvent[] {
  for (let shift = 0; shift <= previous.length; shift += 1) {
    const kept = previous.length - shift;
    if (kept > next.length) continue;
    let matches = true;
    for (let i = 0; i < kept && matches; i += 1) matches = sameEvent(previous[shift + i]!, next[i]!);
    if (matches) return next.slice(kept);
  }
  return [...next];
}

export type BannerTone = "call" | "good" | "bad" | "info";

export interface Banner {
  tone: BannerTone;
  /** Lo grande: "¡TRUCO!", "¡QUIERO!", "Ganaste la ronda". */
  title: string;
  /** Quien y cuanto, en chico. */
  detail: string;
  /** Cuanto dura a la vista (ms). */
  ms: number;
}

const SHOUT: Partial<Record<TrucoEvent["kind"], string>> = {
  truco: "¡TRUCO!",
  retruco: "¡RETRUCO!",
  vale4: "¡VALE CUATRO!",
  envido: "¡ENVIDO!",
  real_envido: "¡REAL ENVIDO!",
  falta_envido: "¡FALTA ENVIDO!",
};

/**
 * El cartel de un hecho, desde el punto de vista de `mySeat` (null = mirando): lo que canta o responde el rival, quien gana cada mano, el envido y la ronda.
 * Devuelve null para los hechos que no merecen cartel (repartir, tirar una carta).
 */
export function bannerFor(event: TrucoEvent, mySeat: number | null, nameOf: (seat: number) => string): Banner | null {
  const mine = mySeat !== null && event.seat === mySeat;
  const who = mine ? "Vos" : nameOf(event.seat);
  const watching = mySeat === null;
  switch (event.kind) {
    case "truco":
    case "retruco":
    case "vale4":
    case "envido":
    case "real_envido":
    case "falta_envido":
      return { tone: "call", title: SHOUT[event.kind]!, detail: mine ? "Cantaste vos" : `${who} te cantó`, ms: 3_500 };
    case "quiero":
      return { tone: "call", title: "¡QUIERO!", detail: mine ? "Respondiste que sí" : `${who} aceptó`, ms: 3_000 };
    case "no_quiero":
      return { tone: watching ? "info" : mine ? "bad" : "good", title: "NO QUIERO", detail: mine ? "Respondiste que no" : `${who} no quiso: sumás los puntos`, ms: 3_500 };
    case "mazo":
      return { tone: watching ? "info" : mine ? "bad" : "good", title: "Al mazo", detail: mine ? "Te fuiste al mazo" : `${who} se fue al mazo`, ms: 3_500 };
    case "baza":
      return { tone: watching ? "info" : mine ? "good" : "bad", title: mine ? "Ganaste la mano" : watching ? `Ganó la mano ${who}` : "Perdiste la mano", detail: "", ms: 2_500 };
    case "parda":
      return { tone: "info", title: "Parda", detail: "La mano empató", ms: 2_500 };
    case "envido_result": {
      const mineWon = mine;
      const points = event.value === null ? "" : ` (+${event.value})`;
      const score = event.a !== null && event.b !== null && mySeat !== null ? ` · ${mySeat === 0 ? event.a : event.b} a ${mySeat === 0 ? event.b : event.a}` : "";
      return { tone: watching ? "info" : mineWon ? "good" : "bad", title: mineWon ? "Ganaste el envido" : watching ? `Envido para ${who}` : "Perdiste el envido", detail: `${points}${score}`.trim(), ms: 4_500 };
    }
    case "hand_end": {
      const points = event.value ?? 0;
      return { tone: watching ? "info" : mine ? "good" : "bad", title: mine ? "¡Ganaste la ronda!" : watching ? `Ronda para ${who}` : "Perdiste la ronda", detail: `+${points} para ${mine ? "vos" : who}`, ms: 4_500 };
    }
    default:
      return null;
  }
}
