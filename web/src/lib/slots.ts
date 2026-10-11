import type { SlotsStep } from "../api/types";

/** Como se ve cada simbolo. Si el servidor agrega uno nuevo en la configuracion, se muestra su nombre. */
const GLYPHS: Record<string, string> = {
  Cereza: "🍒",
  Limon: "🍋",
  Naranja: "🍊",
  Campana: "🔔",
  Bar: "BAR",
  Siete: "7",
};

export const glyphOf = (name: string): string => GLYPHS[name] ?? name;

/** Nombre legible (con tildes) para mostrar en pantalla. */
const LABELS: Record<string, string> = { Limon: "Limón" };
export const labelOf = (name: string): string => LABELS[name] ?? name;

/** Que tan grande fue el premio, segun cuantas veces devolvio la apuesta: decide cuanta fiesta se arma. */
export type WinTier = "win" | "big" | "mega";
export const winTier = (multiplier: number | null): WinTier => ((multiplier ?? 0) >= 50 ? "mega" : (multiplier ?? 0) >= 10 ? "big" : "win");

export const WIN_TITLES: Record<WinTier, string> = { win: "¡Ganaste!", big: "¡GRAN PREMIO!", mega: "¡MEGA PREMIO!" };

/** Que rodillos forman el premio: los tres si salieron iguales; si no, la racha del mismo simbolo desde la izquierda. */
export function winningReels(reels: readonly string[]): boolean[] {
  const first = reels[0];
  let run = 0;
  while (run < reels.length && reels[run] === first) run++;
  return reels.map((_, i) => i < run);
}

/** Cuantos rodillos (desde la izquierda) explotan en un paso: los que forman el premio, solo si ese premio encadena. */
export function explodingReels(step: SlotsStep, minPay: number): boolean[] {
  if (step.pay < minPay) return step.reels.map(() => false);
  return winningReels(step.reels);
}

/** Lo que se ve de la cascada en un momento: los simbolos, cuales explotan, cuales caen nuevos y el multiplicador vigente. */
export interface CascadeView {
  reels: string[];
  bursting: boolean[];
  dropping: boolean[];
  multiplier: number;
  step: number;
}

export interface CascadeFrame {
  /** Milisegundos desde que frenaron los rodillos. */
  at: number;
  view: CascadeView;
}

/** Cuanto dura cada paso de la cascada: un rato explotando y otro cayendo lo nuevo. */
export const CASCADE_BURST_MS = 750;
export const CASCADE_STEP_MS = 1_350;

/**
 * Guion de la cascada: para cada paso, los rodillos del premio explotan y despues caen los simbolos nuevos con el multiplicador mas alto.
 * Es una funcion pura (sin tiempos reales) para poder probarla. `totalMs` es cuando termina la ultima caida.
 */
export function cascadeFrames(steps: readonly SlotsStep[], minPay: number): { frames: CascadeFrame[]; totalMs: number } {
  const frames: CascadeFrame[] = [];
  for (let i = 0; i < steps.length - 1; i++) {
    const here = steps[i]!;
    const next = steps[i + 1]!;
    const exploding = explodingReels(here, minPay);
    const none = here.reels.map(() => false);
    frames.push({ at: i * CASCADE_STEP_MS, view: { reels: here.reels, bursting: exploding, dropping: none, multiplier: here.multiplier, step: i } });
    frames.push({ at: i * CASCADE_STEP_MS + CASCADE_BURST_MS, view: { reels: next.reels, bursting: none, dropping: exploding, multiplier: next.multiplier, step: i + 1 } });
  }
  return { frames, totalMs: Math.max(0, steps.length - 1) * CASCADE_STEP_MS };
}
