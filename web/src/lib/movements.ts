import type { MovementKind } from "../api/types";

const LABELS: Record<MovementKind, string> = {
  WelcomeBonus: "Fichas de bienvenida",
  Credit: "Carga de fichas",
  Stake: "Apuesta",
  Prize: "Premio cobrado",
  Refund: "Apuesta devuelta",
  Reversal: "Operación revertida",
  TransferIn: "Fichas recibidas",
  TransferOut: "Fichas enviadas",
  Commission: "Comisión por cargas",
  Withdrawal: "Retiro solicitado",
  WithdrawalReturned: "Retiro devuelto",
};

/** Texto del concepto de un movimiento. Si el servidor agrega un tipo nuevo, se muestra tal cual. */
export const movementLabel = (kind: MovementKind): string => LABELS[kind] ?? kind;

/** Fichas con signo explicito: "+250" o "−100" (con el signo menos tipografico). */
export function signedChips(value: number, format: (n: number) => string): string {
  if (value === 0) return format(0);
  return `${value > 0 ? "+" : "−"}${format(Math.abs(value))}`;
}

/** Inicio del dia (hora local) de una fecha "AAAA-MM-DD", en ISO. Vacio si no es una fecha valida. */
export function startOfLocalDayIso(date: string): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!match) return null;
  const parsed = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/** Inicio del dia SIGUIENTE (exclusivo): para que "hasta el 9" incluya todo el 9. */
export function endOfLocalDayIso(date: string): string | null {
  const start = startOfLocalDayIso(date);
  if (!start) return null;
  const next = new Date(start);
  next.setDate(next.getDate() + 1);
  return next.toISOString();
}
