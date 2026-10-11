/** Fanfarria corta sintetizada con Web Audio (sin archivos). Silenciosa si el navegador no la soporta. */
export function playWinSound(tier: "win" | "big" | "mega" = "win"): void {
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx();
    const master = ctx.createGain();
    master.gain.value = 0.18;
    master.connect(ctx.destination);

    // Arpegio ascendente do-mi-sol-do y un acorde final.
    // Cuanto mas grande el premio, mas larga y aguda la fanfarria.
    const base = [523.25, 659.25, 783.99, 1046.5];
    const notes = tier === "win" ? base : tier === "big" ? [...base, 1318.5, 1568] : [...base, 1318.5, 1568, 1318.5, 1568, 2093];
    notes.forEach((freq, i) => tone(ctx, master, freq, ctx.currentTime + i * 0.11, 0.22));
    const end = ctx.currentTime + notes.length * 0.11;
    [523.25, 659.25, 783.99, 1046.5].forEach((freq) => tone(ctx, master, freq, end, 0.7));

    window.setTimeout(() => void ctx.close(), 2500);
  } catch {
    /* el sonido es decorativo: nunca debe romper la ronda */
  }
}

function tone(ctx: AudioContext, out: AudioNode, freq: number, start: number, duration: number) {
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = "triangle";
  osc.frequency.value = freq;
  gain.gain.setValueAtTime(0.0001, start);
  gain.gain.exponentialRampToValueAtTime(1, start + 0.02);
  gain.gain.exponentialRampToValueAtTime(0.0001, start + duration);
  osc.connect(gain).connect(out);
  osc.start(start);
  osc.stop(start + duration + 0.05);
}

/** Un "ting" corto que sube de tono con cada cascada: cuanto mas larga la cadena, mas agudo. */
export function playCascadeSound(step: number): void {
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx();
    const master = ctx.createGain();
    master.gain.value = 0.16;
    master.connect(ctx.destination);
    const freq = 440 * Math.pow(2, (Math.min(step, 8) * 3) / 12);
    tone(ctx, master, freq, ctx.currentTime, 0.18);
    tone(ctx, master, freq * 1.5, ctx.currentTime + 0.08, 0.28);
    window.setTimeout(() => void ctx.close(), 800);
  } catch {
    /* el sonido es decorativo: nunca debe romper el giro */
  }
}
