import { ApiError } from "../api/client";
import type { Round, RoundStatus } from "../api/types";

/** Mensajes para el jugador, a partir del codigo estable (`title`) que devuelve la API. */
const ERRORS: Record<string, string> = {
  InsufficientFunds: "No te alcanzan las fichas para esa apuesta.",
  InvalidBet: "Esa apuesta no es válida. Revisá la selección y el monto.",
  InvalidIdempotencyKey: "No se pudo identificar la apuesta. Intentá de nuevo.",
  BetKeyReused: "Esa apuesta ya se envió con otros datos. Intentá de nuevo.",
  AccountNotFound: "Tu cuenta todavía se está preparando. Intentá en unos segundos.",
  PendingBets: "Tenés apuestas sin resolver. Esperá a que terminen.",
  NetworkError: "No hay conexión con el servidor. Revisá tu red e intentá de nuevo.",
  BettingClosed: "Ya se cerró la ronda: esperá a la siguiente para apostar.",
  RoundNotRunning: "El cohete todavía no empezó a subir.",
  CrashedAlready: "El cohete explotó antes de que llegara tu retiro.",
  BetNotActive: "Esa apuesta ya no está en juego.",
  NotYourTurn: "No es tu turno (o se te acabó el tiempo).",
  AlreadySeated: "Ya tenés un asiento en esta mano.",
  TableNotFound: "Esa mesa no existe.",
  RoundNotFound: "Esa mesa no existe o ya no está disponible.",
  TableClosed: "La mesa ya no admite ese cambio (está llena, ya empezó o se cerró).",
  InvalidAction: "Esa jugada no es válida.",
};

/** Como `errorMessage`, pero si el servidor mando un detalle en español (juegos de mesa) lo muestra en lugar del texto generico. */
export function detailedErrorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 401 && error.message !== error.title && !error.message.startsWith("HTTP ") && error.title !== "NetworkError") return error.message;
  return errorMessage(error);
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return ERRORS[error.title] ?? "Algo salió mal. Intentá de nuevo.";
  return "Algo salió mal. Intentá de nuevo.";
}

const STATUS_LABELS: Record<RoundStatus, string> = {
  Placed: "En curso",
  Resolved: "Liquidando",
  Settled: "Cobrada",
  Rejected: "Rechazada",
  Voided: "Anulada",
};

export const statusLabel = (status: RoundStatus) => STATUS_LABELS[status];

/** Lo que paso con las fichas de una ronda, en una frase corta. */
export function outcomeText(round: Pick<Round, "status" | "payout" | "stake">): string {
  switch (round.status) {
    case "Settled":
      return (round.payout ?? 0) > 0 ? `Ganaste ${round.payout}` : `Perdiste ${round.stake}`;
    case "Rejected":
      return "No se jugó";
    case "Voided":
      return "Fichas devueltas";
    default:
      return "En curso";
  }
}

/** Motivo (codigo estable) por el que una ronda no se jugo, en lenguaje del jugador. */
export function failureMessage(reason: string | null): string {
  if (!reason) return "La apuesta no se pudo jugar.";
  return ERRORS[reason] ?? "La apuesta no se pudo jugar.";
}
