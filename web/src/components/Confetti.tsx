import { isLite } from "../perf/perf";

const COLORS = ["#f5c542", "#e0b04a", "#d63a3a", "#f4efe2", "#2fa968"];

/** Lluvia de confeti en CSS puro; el CSS la oculta con prefers-reduced-motion. */
/** Pseudoaleatorio determinista (0..1) para que el render sea puro. */
const rand = (seed: number) => {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return x - Math.floor(x);
};

export function Confetti({ pieces = 40 }: { pieces?: number }) {
  const count = isLite() ? Math.ceil(pieces / 4) : pieces;
  const items = Array.from({ length: count }, (_, i) => ({
    left: rand(i + 1) * 100,
    delay: rand(i + 101) * 0.6,
    duration: 1.8 + rand(i + 201) * 1.6,
    drift: (rand(i + 301) - 0.5) * 160,
    color: COLORS[i % COLORS.length],
  }));
  return (
    <div className="confetti" aria-hidden="true" data-testid="confetti">
      {items.map((p, i) => (
        <span
          key={i}
          className="confetti__piece"
          style={{
            left: `${p.left}%`,
            background: p.color,
            animationDelay: `${p.delay}s`,
            animationDuration: `${p.duration}s`,
            ["--drift" as string]: `${p.drift}px`,
          }}
        />
      ))}
    </div>
  );
}
