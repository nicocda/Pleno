import { glyphOf } from "../lib/slots";
import type { SlotThemeId } from "../lib/slotThemes";

type Pt = readonly [number, number];
type Tone = "light" | "mid" | "dark";
interface Facet {
  points: string;
  tone: Tone;
}
interface Gem {
  colors: Record<Tone, string>;
  facets: Facet[];
  /** El simbolo mas valioso brilla y se destaca. */
  top?: boolean;
}

const fmt = (pts: Pt[]) => pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(" ");

/** La luz viene de arriba a la izquierda: las caras que miran hacia ahi son claras y las opuestas oscuras. */
const toneOf = (angle: number): Tone => {
  const light = -0.707 * Math.sin(angle) + 0.707 * Math.cos(angle);
  return light > 0.35 ? "light" : light < -0.35 ? "dark" : "mid";
};

/** Piedra tallada con `n` lados: un anillo de caras alrededor de una mesa central. */
function cut(n: number, rot: number, rx: number, ry: number, inner: number): Facet[] {
  const at = (i: number, k: number): Pt => {
    const a = rot + (2 * Math.PI * i) / n;
    return [32 + rx * k * Math.sin(a), 32 - ry * k * Math.cos(a)];
  };
  const facets: Facet[] = [];
  for (let i = 0; i < n; i++) {
    const mid = rot + (2 * Math.PI * (i + 0.5)) / n;
    facets.push({ points: fmt([at(i, 1), at(i + 1, 1), at(i + 1, inner), at(i, inner)]), tone: toneOf(mid) });
  }
  facets.push({ points: fmt(Array.from({ length: n }, (_, i) => at(i, inner))), tone: "mid" });
  return facets;
}

const DIAMOND: Facet[] = [
  { points: "18,12 46,12 40,26 24,26", tone: "light" },
  { points: "18,12 24,26 5,26", tone: "mid" },
  { points: "46,12 59,26 40,26", tone: "mid" },
  { points: "5,26 24,26 32,58", tone: "dark" },
  { points: "24,26 40,26 32,58", tone: "light" },
  { points: "40,26 59,26 32,58", tone: "dark" },
];

/** Cada simbolo del servidor es una gema: de la mas comun (rubi) a la mas valiosa (diamante). */
const GEMS: Record<string, Gem> = {
  Cereza: { colors: { light: "#ff9aa8", mid: "#e0203f", dark: "#7d0a22" }, facets: cut(14, 0, 26, 26, 0.5) },
  Limon: { colors: { light: "#fff59a", mid: "#f2d21b", dark: "#a88a00" }, facets: cut(6, 0, 27, 27, 0.5) },
  Naranja: { colors: { light: "#ffd08a", mid: "#ff9a2e", dark: "#b85a00" }, facets: cut(8, Math.PI / 8, 28, 28, 0.55) },
  Campana: { colors: { light: "#7df5b0", mid: "#1fbf6f", dark: "#0b6b3f" }, facets: cut(4, Math.PI / 4, 27, 27, 0.55) },
  Bar: { colors: { light: "#8fb4ff", mid: "#3566e0", dark: "#16318f" }, facets: cut(14, 0, 21, 28, 0.5) },
  Siete: { colors: { light: "#ffffff", mid: "#bfe9ff", dark: "#6fb4e8" }, facets: DIAMOND, top: true },
};

/** Destello de cuatro puntas que titila sobre cada gema. */
const GLINT = "M0,-7 L1.6,-1.6 L7,0 L1.6,1.6 L0,7 L-1.6,1.6 L-7,0 L-1.6,-1.6 Z";

/**
 * Dibuja un simbolo de la tragamonedas segun el tema. Las gemas son SVG hecho por codigo (sin imagenes).
 * Es decorativo: el nombre accesible lo da la maquina (`role="img"` con lo que salio).
 */
export function SlotSymbol({ theme, name }: { theme: SlotThemeId; name: string }) {
  const gem = theme === "gemas" ? GEMS[name] : undefined;
  if (!gem) return <span className="glyph">{glyphOf(name)}</span>;

  return (
    <svg className={`gem ${gem.top ? "gem--top" : ""}`} viewBox="0 0 64 64" aria-hidden="true" focusable="false">
      {gem.facets.map((f, i) => (
        <polygon key={i} points={f.points} fill={gem.colors[f.tone]} stroke={gem.colors.dark} strokeWidth="0.8" strokeLinejoin="round" />
      ))}
      <path className="gem__glint" d={GLINT} transform="translate(22 21)" fill="#fff" />
    </svg>
  );
}
