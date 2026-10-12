import { useMemo, useRef } from "react";
import { cellOf, chipBreakdown, COLUMNS, edgeSpots, outsideSpots, ROWS, type PlacedChips, type Spot } from "../lib/board";
import { formatChips } from "../lib/format";
import { pocketColor } from "../lib/roulette";

/** Cuanto hay que mantener el dedo sobre un lugar para sacar una ficha (en pantallas tactiles no hay clic derecho). */
const LONG_PRESS_MS = 400;

interface Props {
  /** Lo apostado hasta ahora: se puede apostar a varios lugares a la vez. */
  placed: readonly PlacedChips[];
  disabled: boolean;
  /** Numero ganador de la ultima tirada, para resaltarlo en el tapete. */
  winning: number | null;
  onPick: (spot: Spot) => void;
  /** Clic derecho sobre un lugar: saca la ultima ficha que se puso ahi. */
  onRemove?: (spot: Spot) => void;
}

const OUTSIDE_NAMES: Record<string, string> = { Low: "1-18", Even: "Par", Red: "Rojo", Black: "Negro", Odd: "Impar", High: "19-36" };

/** El tapete: 37 numeros y todos los puntos de apuesta de los bordes (caballos, calles, cuadros, seisenas). */
export function Board({ placed, disabled, winning, onPick, onRemove }: Props) {
  const edges = useMemo(() => edgeSpots(), []);
  const outside = useMemo(() => outsideSpots(), []);
  const dozens = outside.filter((s) => s.kind === "dozen");
  const columns = outside.filter((s) => s.kind === "column");
  const chances = outside.filter((s) => s.kind === "outside");
  const halves = chances.filter((s) => s.betType === "Low" || s.betType === "High");
  const evens = chances.filter((s) => s.betType !== "Low" && s.betType !== "High");
  const stakes = useMemo(() => new Map(placed.map((p) => [p.spot.id, p.stake])), [placed]);
  const isPlaced = (s: Spot) => stakes.has(s.id);
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const longPressed = useRef(false);
  const cancelPress = () => {
    if (pressTimer.current) clearTimeout(pressTimer.current);
    pressTimer.current = null;
  };
  /** Sacar una ficha de un lugar: clic derecho con mouse, toque largo con el dedo. */
  const remove = (spot: Spot) => ({
    onContextMenu: (e: React.MouseEvent) => {
      if (!onRemove) return;
      e.preventDefault();
      // En Android el toque largo tambien dispara el menu contextual: ya se saco con el temporizador.
      if (!longPressed.current) onRemove(spot);
    },
    onPointerDown: (e: React.PointerEvent) => {
      longPressed.current = false;
      if (!onRemove || e.pointerType === "mouse") return;
      cancelPress();
      pressTimer.current = setTimeout(() => {
        longPressed.current = true;
        navigator.vibrate?.(15);
        onRemove(spot);
      }, LONG_PRESS_MS);
    },
    onPointerUp: cancelPress,
    onPointerLeave: cancelPress,
    onPointerCancel: cancelPress,
    // Tras un toque largo el clic que sigue no debe sumar una ficha.
    onClickCapture: (e: React.MouseEvent) => {
      if (!longPressed.current) return;
      longPressed.current = false;
      e.stopPropagation();
    },
  });

  const numberButton = (n: number, style: React.CSSProperties) => {
    const id = `Straight:${n}`;
    const spot: Spot = { id, kind: "number", betType: "Straight", selection: [n], covered: 1, multiplier: 36, label: `Pleno ${n}` };
    const stake = stakes.get(id);
    const on = stake !== undefined;
    return (
      <button
        key={n}
        type="button"
        aria-label={`Pleno ${n}`}
        aria-pressed={on}
        disabled={disabled}
        className={`cell cell--${pocketColor(n)} ${on ? "cell--on" : ""} ${winning === n ? "cell--winner" : ""}`}
        style={style}
        onClick={() => onPick(spot)}
        {...remove(spot)}
      >
        {n}
        {stake !== undefined && <Stack stake={stake} />}
      </button>
    );
  };

  return (
    <div className="board" data-testid="board">
      <div className="board__numbers">
        {numberButton(0, { gridColumn: 1, gridRow: `1 / span ${ROWS}` })}
        {Array.from({ length: 36 }, (_, i) => i + 1).map((n) => {
          const { column, row } = cellOf(n);
          return numberButton(n, { gridColumn: column + 2, gridRow: row + 1 });
        })}

        {edges.map((s) => (
          <button
            key={s.id}
            type="button"
            aria-label={s.label}
            aria-pressed={isPlaced(s)}
            disabled={disabled}
            className={`hotspot hotspot--${s.kind} ${isPlaced(s) ? "hotspot--on" : ""}`}
            style={{ left: `${((s.x! + 1) / (COLUMNS + 1)) * 100}%`, top: `${(s.y! / ROWS) * 100}%` }}
            onClick={() => onPick(s)}
            {...remove(s)}
          >
            {isPlaced(s) && <Stack stake={stakes.get(s.id)!} />}
          </button>
        ))}
      </div>

      <div className="board__outside">
        <div className="board__row board__row--3">
          {dozens.map((s) => (
            <OutsideButton key={s.id} spot={s} text={`${s.selection[0]}ª docena`} on={isPlaced(s)} disabled={disabled} stake={stakes.get(s.id)} onPick={onPick} onRemove={remove} />
          ))}
        </div>
        <div className="board__row board__row--3">
          {columns.map((s) => (
            <OutsideButton key={s.id} spot={s} text={`Columna ${s.selection[0]}`} on={isPlaced(s)} disabled={disabled} stake={stakes.get(s.id)} onPick={onPick} onRemove={remove} />
          ))}
        </div>
        <div className="board__row board__row--2">
          {halves.map((s) => (
            <OutsideButton key={s.id} spot={s} text={OUTSIDE_NAMES[s.betType]!} on={isPlaced(s)} disabled={disabled} stake={stakes.get(s.id)} onPick={onPick} onRemove={remove} tone={s.betType === "Red" ? "red" : s.betType === "Black" ? "black" : undefined} />
          ))}
        </div>
        <div className="board__row board__row--4">
          {evens.map((s) => (
            <OutsideButton key={s.id} spot={s} text={OUTSIDE_NAMES[s.betType]!} on={isPlaced(s)} disabled={disabled} stake={stakes.get(s.id)} onPick={onPick} onRemove={remove} tone={s.betType === "Red" ? "red" : s.betType === "Black" ? "black" : undefined} />
          ))}
        </div>
      </div>
    </div>
  );
}

function OutsideButton({ spot, text, on, disabled, stake, onPick, onRemove, tone }: { spot: Spot; text: string; on: boolean; disabled: boolean; stake?: number; onPick: (s: Spot) => void; onRemove: (s: Spot) => React.HTMLAttributes<HTMLButtonElement>; tone?: "red" | "black" }) {
  return (
    <button type="button" aria-label={spot.label} aria-pressed={on} disabled={disabled} className={`outside ${tone ? `outside--${tone}` : ""} ${on ? "outside--on" : ""}`} onClick={() => onPick(spot)} {...onRemove(spot)}>
      {text}
      <small>x{spot.multiplier}</small>
      {on && stake !== undefined && <Stack stake={stake} />}
    </button>
  );
}

/** Pila de fichas sobre el casillero, con el monto total. */
function Stack({ stake }: { stake: number }) {
  const pieces = chipBreakdown(stake).flatMap(({ value, count }) => Array.from({ length: Math.min(count, 3) }, () => value));
  return (
    <span className="stack-chips" aria-hidden="true" data-testid="chip-stack">
      {pieces.slice(0, 4).map((value, i) => (
        <span key={i} className={`chip chip--${value}`} style={{ bottom: i * 3 }} />
      ))}
      <span className="stack-chips__total">{formatChips(stake)}</span>
    </span>
  );
}
