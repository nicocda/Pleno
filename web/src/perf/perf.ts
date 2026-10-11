export type PerfChoice = "auto" | "full" | "lite";
export type PerfMode = "full" | "lite";

export const PERF_STORAGE_KEY = "pleno.perf";

export function readPerfChoice(): PerfChoice {
  try {
    const stored = window.localStorage.getItem(PERF_STORAGE_KEY);
    return stored === "full" || stored === "lite" ? stored : "auto";
  } catch {
    return "auto"; // almacenamiento bloqueado (modo privado): se sigue en automatico
  }
}

/** Heuristica barata para detectar un equipo con pocos recursos (los navegadores que no exponen el dato cuentan como "no sabemos"). */
export function looksLowEnd(): boolean {
  if (typeof navigator === "undefined") return false;
  const { hardwareConcurrency, deviceMemory } = navigator as Navigator & { deviceMemory?: number };
  const saveData = (navigator as Navigator & { connection?: { saveData?: boolean } }).connection?.saveData === true;
  const wantsLessData = window.matchMedia?.("(prefers-reduced-data: reduce)").matches ?? false;
  return (hardwareConcurrency !== undefined && hardwareConcurrency <= 4) || (deviceMemory !== undefined && deviceMemory <= 3) || saveData || wantsLessData;
}

export const resolvePerfMode = (choice: PerfChoice): PerfMode => (choice === "auto" ? (looksLowEnd() ? "lite" : "full") : choice);

/** Modo vigente, tal como lo dejo el `PerfProvider` en <html>. Lo leen los canvas al crearse. */
export const isLite = (): boolean => typeof document !== "undefined" && document.documentElement.dataset.perf === "lite";

/** Resolucion del canvas: 1x en modo ligero, y nunca mas de 2x (3x pinta 9 veces los pixeles sin que se note). */
export const canvasResolution = (): number => (isLite() ? 1 : Math.min(window.devicePixelRatio || 1, 2));
