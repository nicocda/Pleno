import { useEffect, useRef } from "react";
import { type CrashView, ONE_X } from "../lib/crash";

interface Props {
  /** Devuelve lo que hay que dibujar AHORA. Se llama en cada cuadro, sin pasar por React, para que la curva sea suave. */
  getView: () => CrashView;
  growthPerSecond: number;
  label: string;
}

const W = 640;
const H = 320;
const PAD = 28;

/** La curva del multiplicador y el cohete. Solo dibuja: toda la logica (tiempos, multiplicador) esta en `lib/crash`. */
export function CrashCanvas({ getView, growthPerSecond, label }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const getViewRef = useRef(getView);
  useEffect(() => {
    getViewRef.current = getView;
  });

  useEffect(() => {
    const element = canvas.current;
    const ctx = element?.getContext("2d");
    if (!element || !ctx) return; // sin canvas (por ejemplo en las pruebas) la pagina igual funciona

    let frame = 0;
    const draw = () => {
      const view = getViewRef.current();
      ctx.clearRect(0, 0, W, H);
      const bg = ctx.createLinearGradient(0, 0, 0, H);
      bg.addColorStop(0, "#0b1220");
      bg.addColorStop(1, "#101c33");
      ctx.fillStyle = bg;
      ctx.fillRect(0, 0, W, H);

      const live = view.phase === "running" || view.phase === "crashed";
      const maxMultiplier = Math.max(2, (view.multiplier / 100) * 1.2);
      const duration = Math.max(8, view.phase === "crashed" ? Math.log(view.multiplier / 100) / growthPerSecond : view.elapsed);
      const x = (t: number) => PAD + (t / (duration * 1.1)) * (W - PAD * 2);
      const y = (m: number) => H - PAD - ((m - 1) / (maxMultiplier - 1)) * (H - PAD * 2);

      // Ejes y guias.
      ctx.strokeStyle = "rgb(255 255 255 / 0.12)";
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(PAD, PAD);
      ctx.lineTo(PAD, H - PAD);
      ctx.lineTo(W - PAD, H - PAD);
      ctx.stroke();

      if (live) {
        const end = view.phase === "crashed" ? Math.log(view.multiplier / 100) / growthPerSecond : view.elapsed;
        const exploded = view.phase === "crashed";
        ctx.strokeStyle = exploded ? "#ff6b6b" : "#3ecf8e";
        ctx.lineWidth = 4;
        ctx.beginPath();
        for (let t = 0; t <= end; t += end / 80 || 1) {
          const m = Math.exp(growthPerSecond * t);
          if (t === 0) ctx.moveTo(x(t), y(m));
          else ctx.lineTo(x(t), y(m));
        }
        ctx.lineTo(x(end), y(Math.exp(growthPerSecond * end)));
        ctx.stroke();

        ctx.font = "34px system-ui, sans-serif";
        ctx.textAlign = "center";
        ctx.fillText(exploded ? "💥" : "🚀", x(end), y(Math.exp(growthPerSecond * end)) - 6);
      } else {
        ctx.fillStyle = "rgb(255 255 255 / 0.5)";
        ctx.font = "20px system-ui, sans-serif";
        ctx.textAlign = "center";
        ctx.fillText(view.phase === "betting" ? "Hacé tu apuesta" : "Esperando la próxima ronda…", W / 2, H / 2);
      }

      ctx.fillStyle = "#fff";
      ctx.font = "600 12px system-ui, sans-serif";
      ctx.textAlign = "left";
      ctx.fillText(`x${(ONE_X / 100).toFixed(2)}`, 4, H - PAD + 4);
      frame = requestAnimationFrame(draw);
    };
    frame = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(frame);
  }, [growthPerSecond]);

  return <canvas ref={canvas} width={W} height={H} className="crash-canvas" role="img" aria-label={label} data-testid="crash-canvas" />;
}
