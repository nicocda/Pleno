import { Application, Container, Graphics, Text } from "pixi.js";
import { useEffect, useRef } from "react";
import { canvasResolution, isLite } from "../perf/perf";
import { pocketColor } from "../lib/roulette";
import { POCKET_STEP, POCKETS, WHEEL_ORDER, type WheelModel, type WheelPhase } from "../lib/wheel";

const SIZE = 360;
const RADIUS = SIZE / 2 - 6;
const COLORS = { red: 0xb3261e, black: 0x16161a, green: 0x1b7a43 } as const;

/** Casillero i del giro: sector entre dos angulos (0 = las 12 en punto, horario). */
function drawWheel(wheel: Graphics) {
  wheel.clear();
  wheel.circle(0, 0, RADIUS).fill(0x3a2a14);
  for (let i = 0; i < POCKETS; i++) {
    const from = i * POCKET_STEP - Math.PI / 2;
    const to = (i + 1) * POCKET_STEP - Math.PI / 2;
    wheel
      .moveTo(0, 0)
      .arc(0, 0, RADIUS * 0.86, from, to)
      .lineTo(0, 0)
      .fill(COLORS[pocketColor(WHEEL_ORDER[i]!)])
      .stroke({ width: 1, color: 0xd9b45a, alpha: 0.7 });
  }
  wheel.circle(0, 0, RADIUS * 0.52).fill(0x241a0c).stroke({ width: 3, color: 0xd9b45a });
  wheel.circle(0, 0, RADIUS * 0.12).fill(0xd9b45a);
}

interface Props {
  model: WheelModel;
  /** Fase actual, para `data-state` (lo usan las pruebas end-to-end). */
  phase: WheelPhase;
  /** Texto accesible con el estado actual. */
  label: string;
}

/**
 * Dibuja la rueda con PixiJS. No decide ni avanza nada: solo lee los angulos del `WheelModel` en cada cuadro.
 * El avance del tiempo lo hace `useWheelClock`, asi la logica funciona (y se prueba) aunque no haya WebGL.
 */
export function WheelCanvas({ model, phase, label }: Props) {
  const host = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const element = host.current;
    if (!element) return;

    // En StrictMode el efecto corre dos veces: `cancelled` evita usar una aplicacion que ya se destruyo.
    let cancelled = false;
    const app = new Application();
    let ready = false;

    void app
      .init({ width: SIZE, height: SIZE, backgroundAlpha: 0, antialias: !isLite(), resolution: canvasResolution(), autoDensity: true })
      .then(() => {
        if (cancelled) {
          app.destroy(true);
          return;
        }
        ready = true;
        const canvas = app.canvas;
        canvas.style.width = "100%";
        canvas.style.height = "auto";
        canvas.setAttribute("aria-hidden", "true");
        element.appendChild(canvas);

        const stage = new Container();
        stage.position.set(SIZE / 2, SIZE / 2);
        const wheel = new Container(); // gira completo: disco + numeros
        const disc = new Graphics();
        drawWheel(disc);
        wheel.addChild(disc);
        const labels = new Container();
        for (let i = 0; i < POCKETS; i++) {
          const angle = (i + 0.5) * POCKET_STEP - Math.PI / 2;
          const text = new Text({ text: String(WHEEL_ORDER[i]), style: { fill: 0xffffff, fontSize: 11, fontWeight: "700" } });
          text.anchor.set(0.5);
          text.position.set(Math.cos(angle) * RADIUS * 0.74, Math.sin(angle) * RADIUS * 0.74);
          text.rotation = angle + Math.PI / 2;
          labels.addChild(text);
        }
        wheel.addChild(labels);
        const ball = new Graphics().circle(0, 0, 7).fill(0xf5f1e6).stroke({ width: 1, color: 0x999999 });
        stage.addChild(wheel, ball);
        app.stage.addChild(stage);

        // Marca fija arriba: apunta al casillero que esta a las 12.
        const marker = new Graphics().poly([-7, -SIZE / 2 + 2, 7, -SIZE / 2 + 2, 0, -SIZE / 2 + 18]).fill(0xd9b45a);
        app.stage.addChild(marker);
        marker.position.set(SIZE / 2, SIZE / 2);

        if (isLite()) app.ticker.maxFPS = 30;
        app.ticker.add(() => {
          wheel.rotation = model.wheelAngle;
          const r = model.ballRadius * RADIUS;
          const a = model.ballAngle - Math.PI / 2;
          ball.position.set(Math.cos(a) * r, Math.sin(a) * r);

        });
      })
      .catch(() => {
        // Sin WebGL/Canvas (por ejemplo en jsdom): la ruleta sigue funcionando sin dibujo.
      });

    return () => {
      cancelled = true;
      if (ready) {
        element.replaceChildren();
        app.destroy(true);
      }
    };
  }, [model]);

  return <div ref={host} className="wheel" role="img" aria-label={label} data-state={phase} data-testid="wheel" />;
}
