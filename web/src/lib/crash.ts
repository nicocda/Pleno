import type { CrashRound } from "../api/types";

// Las matematicas de Crash en el navegador: el multiplicador que se dibuja y la VERIFICACION de una ronda (recalcular el punto de explosion con
// la semilla revelada). Los multiplicadores son enteros en centesimas (250 = x2,50), igual que en el servidor.

export const ONE_X = 100;
export const MAX_MULTIPLIER = 10_000;

/** El multiplicador (centesimas) a los `elapsedSeconds` de empezar a subir. Solo para DIBUJAR: el que cuenta para los pagos lo decide el servidor. */
export function multiplierAt(elapsedSeconds: number, growthPerSecond: number): number {
  if (elapsedSeconds <= 0) return ONE_X;
  const value = Math.floor(100 * Math.exp(growthPerSecond * elapsedSeconds));
  return value >= MAX_MULTIPLIER ? MAX_MULTIPLIER : Math.max(ONE_X, value);
}

/** Cuantos segundos tarda el multiplicador en llegar a `multiplier` (centesimas). */
export function timeToReach(multiplier: number, growthPerSecond: number): number {
  return multiplier <= ONE_X ? 0 : Math.log(multiplier / 100) / growthPerSecond;
}

const formatter = new Intl.NumberFormat("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** 250 -> "2,50x" */
export const formatMultiplier = (centi: number): string => `${formatter.format(centi / 100)}x`;

/** Lo que escribe el jugador ("2,5" o "2.5") como decimal valido para el retiro automatico (1,01 a 100), o null. */
export function parseAutoCashOut(text: string): number | null {
  const normalized = text.trim().replace(",", ".");
  if (!/^\d{1,4}(\.\d{1,2})?$/.test(normalized)) return null;
  const value = Number(normalized);
  return value > 1 && value <= 100 ? value : null;
}

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * El punto de explosion de una ronda, recalculado desde la semilla revelada. Es el MISMO algoritmo que usa el servidor
 * (docs/juego-crash.md): HMAC-SHA256 con la semilla como clave y "crash:{id sin guiones}" como mensaje; se toman 52 bits (r) y el punto es
 * piso(100 * (1000 - ventaja) * 2^52 / (1000 * (2^52 - r))), entre x1,00 y x100. BigInt: ningun paso pierde precision.
 */
export async function crashPointOf(serverSeed: string, roundId: string, edgePermille: number): Promise<number> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(serverSeed), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`crash:${roundId.replaceAll("-", "")}`)));
  let top56 = 0n;
  for (let i = 0; i < 7; i += 1) top56 = (top56 << 8n) | BigInt(mac[i]!);

  const r = top56 >> 4n; // 52 bits
  const scale = 1n << 52n;
  const raw = (100n * BigInt(1000 - edgePermille) * scale) / (1000n * (scale - r));
  if (raw < BigInt(ONE_X)) return ONE_X;
  return raw > BigInt(MAX_MULTIPLIER) ? MAX_MULTIPLIER : Number(raw);
}

export interface RoundVerification {
  /** La semilla revelada coincide con el compromiso publicado antes de apostar. */
  commitmentOk: boolean;
  /** El punto de explosion que informo el servidor coincide con el recalculado. */
  crashPointOk: boolean;
  computedCrashPoint: number;
}

/** Verifica una ronda ya terminada. Devuelve null si todavia no se revelo la semilla. */
export async function verifyRound(round: CrashRound): Promise<RoundVerification | null> {
  if (round.serverSeed === null || round.crashPoint === null) return null;
  const computed = await crashPointOf(round.serverSeed, round.id, round.edgePermille);
  return {
    commitmentOk: (await sha256Hex(round.serverSeed)) === round.commitment.toLowerCase(),
    crashPointOk: computed === round.crashPoint,
    computedCrashPoint: computed,
  };
}

export type CrashPhaseView = "waiting" | "betting" | "running" | "crashed" | "aborted";

export interface CrashView {
  phase: CrashPhaseView;
  /** Lo que se muestra (centesimas): sube mientras corre y queda en el punto de explosion al terminar. */
  multiplier: number;
  /** Segundos que faltan para que cierre la ventana de apuestas (solo en `betting`). */
  secondsLeft: number;
  /** Segundos que lleva subiendo (solo en `running`). */
  elapsed: number;
}

/** Lo que hay que mostrar en este instante, a partir de la ronda que informo el servidor y la hora del servidor. Es pura: se prueba sin navegador. */
export function crashView(round: CrashRound | null, serverNowMs: number): CrashView {
  if (!round) return { phase: "waiting", multiplier: ONE_X, secondsLeft: 0, elapsed: 0 };

  switch (round.phase) {
    case "Betting":
      return { phase: "betting", multiplier: ONE_X, secondsLeft: Math.max(0, (Date.parse(round.bettingEndsAt) - serverNowMs) / 1000), elapsed: 0 };
    case "Running": {
      const elapsed = Math.max(0, (serverNowMs - Date.parse(round.startedAt ?? round.bettingEndsAt)) / 1000);
      return { phase: "running", multiplier: multiplierAt(elapsed, round.growthPerSecond), secondsLeft: 0, elapsed };
    }
    case "Crashed":
      return { phase: "crashed", multiplier: round.crashPoint ?? ONE_X, secondsLeft: 0, elapsed: 0 };
    default:
      return { phase: "aborted", multiplier: ONE_X, secondsLeft: 0, elapsed: 0 };
  }
}
