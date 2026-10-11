import { useState } from "react";
import { formatChips } from "../lib/format";

interface StakePickerProps {
  /** El monto elegido (fichas enteras). */
  stake: number;
  onChange: (stake: number) => void;
  /** Valores rapidos: se muestran solo los que entran en el rango de la mesa. */
  chips: readonly number[];
  min: number;
  max: number;
  disabled?: boolean;
  /** Con `false` solo se eligen las fichas rapidas (sin campo para escribir otro monto). */
  allowCustom?: boolean;
  /** El monto al que se vuelve al borrar el campo "otro monto". */
  defaultStake?: number;
  label?: string;
}

/**
 * "Cuantas fichas": fichas de seleccion rapida y, al final, un campo para escribir otro monto. Siempre en UNA sola fila (sin saltos de linea,
 * tambien en el celular: las fichas se achican). Compartido por todos los juegos.
 */
export function StakePicker({ stake, onChange, chips, min, max, disabled = false, allowCustom = true, defaultStake = min, label = "Fichas a apostar" }: StakePickerProps) {
  const [custom, setCustom] = useState("");
  const valid = Number.isInteger(stake) && stake >= min && stake <= max;

  return (
    <div className="chips chips--stake" role="radiogroup" aria-label={label}>
      {chips.filter((value) => value >= min && value <= max).map((value) => (
        <button
          key={value}
          type="button"
          role="radio"
          aria-checked={stake === value}
          disabled={disabled}
          className={`choice choice--stake ${stake === value ? "choice--on" : ""}`}
          onClick={() => {
            onChange(value);
            setCustom("");
          }}
        >
          {formatChips(value)}
        </button>
      ))}
      {allowCustom && (
        <input
          className={`chip-input ${custom === "" ? "" : "chip-input--on"}`}
          type="number" inputMode="numeric" min={min} max={max} step={1}
          placeholder="✎" aria-label={`Otro monto (${formatChips(min)} a ${formatChips(max)})`}
          value={custom} disabled={disabled} aria-invalid={custom !== "" && !valid}
          onChange={(e) => {
            setCustom(e.target.value);
            onChange(e.target.value === "" ? defaultStake : Math.trunc(Number(e.target.value)));
          }}
        />
      )}
    </div>
  );
}
