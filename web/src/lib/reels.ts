// Modelo de los rodillos: pura logica de tiempo, sin dibujo. Asi es determinista y se prueba sin navegador.
// Mientras gira, cada rodillo muestra simbolos que cambian rapido; cuando se conoce el resultado y pasa el tiempo minimo,
// los rodillos se frenan de izquierda a derecha, uno tras otro, cada uno en el simbolo que ya decidio el servidor.

export type ReelsPhase = "idle" | "spinning" | "settled";

export interface ReelsTimings {
  /** Tiempo minimo girando antes de frenar el primer rodillo, para que el suspenso no dependa de la velocidad del servidor. */
  minSpinMs: number;
  /** Cuanto despues frena cada rodillo respecto del anterior. */
  staggerMs: number;
  /** Cada cuanto cambia el simbolo que se ve en un rodillo que gira. */
  cycleMs: number;
  /** Suspenso extra del ultimo rodillo cuando los anteriores ya salieron iguales (casi-premio): frena mucho mas lento. */
  tensionMs: number;
}

export const DEFAULT_REEL_TIMINGS: ReelsTimings = { minSpinMs: 1_100, staggerMs: 450, cycleMs: 70, tensionMs: 1_100 };

export class ReelsModel {
  phase: ReelsPhase = "idle";
  /** Indice del simbolo que muestra cada rodillo. */
  readonly display: number[];
  /** Si cada rodillo ya se detuvo. */
  readonly stopped: boolean[];
  /** Simbolos finales (indices), una vez conocidos. */
  result: number[] | null = null;

  private readonly timings: ReelsTimings;
  private elapsed = 0;
  private cycleClock = 0;
  private pending: number[] | null = null;

  private readonly symbolCount: number;
  private readonly reelCount: number;
  private readonly options: { timings?: Partial<ReelsTimings>; reduceMotion?: boolean };

  constructor(symbolCount: number, reelCount = 3, options: { timings?: Partial<ReelsTimings>; reduceMotion?: boolean } = {}) {
    if (symbolCount < 2) throw new RangeError("Hacen falta al menos 2 simbolos");
    this.symbolCount = symbolCount;
    this.reelCount = reelCount;
    this.options = options;
    this.timings = { ...DEFAULT_REEL_TIMINGS, ...options.timings };
    this.display = Array.from({ length: reelCount }, (_, i) => i % symbolCount);
    this.stopped = Array.from({ length: reelCount }, () => true);
  }

  /** Empieza a girar (se llama al enviar la apuesta, sin conocer todavia el resultado). */
  spin(): void {
    this.phase = "spinning";
    this.result = null;
    this.pending = null;
    this.elapsed = 0;
    this.cycleClock = 0;
    this.stopped.fill(false);
  }

  /** Informa los simbolos finales. Si todavia no paso el tiempo minimo, los rodillos frenan apenas se cumpla. */
  land(result: readonly number[]): void {
    if (result.length !== this.reelCount || result.some((s) => !Number.isInteger(s) || s < 0 || s >= this.symbolCount)) {
      throw new RangeError("Resultado de rodillos invalido");
    }
    if (this.phase !== "spinning") this.spin();

    if (this.options.reduceMotion) {
      this.finish(result);
      return;
    }
    this.pending = [...result];
  }

  /** La apuesta no llego a jugarse: los rodillos vuelven al reposo. */
  cancel(): void {
    this.phase = "idle";
    this.result = null;
    this.pending = null;
    this.stopped.fill(true);
  }

  /** Los rodillos anteriores ya frenaron en el mismo simbolo y el ultimo todavia gira: momento de suspenso. */
  get anticipating(): boolean {
    const last = this.reelCount - 1;
    return this.phase === "spinning" && this.pending !== null && this.leadingMatch() && !this.stopped[last] && this.stopped.slice(0, last).every(Boolean);
  }

  /** Cuando frena el rodillo `i` (ms desde que empezo el giro), segun el resultado que ya se conoce. */
  private stopAt(i: number): number {
    const tense = i === this.reelCount - 1 && this.leadingMatch();
    return this.timings.minSpinMs + i * this.timings.staggerMs + (tense ? this.timings.tensionMs : 0);
  }

  /** Todos los rodillos menos el ultimo salieron en el mismo simbolo (hace falta mas de un rodillo anterior para que haya suspenso). */
  private leadingMatch(): boolean {
    const result = this.pending;
    if (!result || this.reelCount < 3) return false;
    return result.slice(0, -1).every((s) => s === result[0]);
  }

  update(deltaMs: number): void {
    if (this.phase !== "spinning") return;
    this.elapsed += deltaMs;
    this.cycleClock += deltaMs;

    const steps = Math.floor(this.cycleClock / this.timings.cycleMs);
    this.cycleClock -= steps * this.timings.cycleMs;

    for (let i = 0; i < this.reelCount; i++) {
      if (this.stopped[i]) continue;
      if (this.pending && this.elapsed >= this.stopAt(i)) {
        this.display[i] = this.pending[i]!;
        this.stopped[i] = true;
      } else {
        // Cada rodillo avanza a su ritmo (un poco distinto) para que no se vean clonados.
        this.display[i] = (this.display[i]! + steps * (1 + (i % 2))) % this.symbolCount;
      }
    }

    if (this.pending && this.stopped.every(Boolean)) {
      this.result = this.pending;
      this.pending = null;
      this.phase = "settled";
    }
  }

  private finish(result: readonly number[]): void {
    result.forEach((symbol, i) => {
      this.display[i] = symbol;
      this.stopped[i] = true;
    });
    this.result = [...result];
    this.pending = null;
    this.phase = "settled";
  }
}
