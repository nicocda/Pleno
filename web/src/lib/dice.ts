import type { DiceDirection } from "../api/types";

/** Los mismos limites que el servidor (`DiceBet`): la probabilidad va en centesimas de punto porcentual. */
export const DICE_ROLLS = 10_000;
export const MIN_CHANCE = 200;
export const MAX_CHANCE = 9_500;
export const MIN_STAKE = 1;
export const MAX_STAKE = 10_000;
const PAYOUT_NUMERATOR = 9_800;

/** 4950 -> "49,50". */
export const formatHundredths = (value: number) => (value / 100).toFixed(2).replace(".", ",");

/** Lo que se cobra (apuesta incluida) si gana: apuesta x 9800 / probabilidad, redondeado hacia abajo. */
export const payoutFor = (stake: number, chance: number) => Math.floor((stake * PAYOUT_NUMERATOR) / chance);

/** Multiplicador con dos decimales (para mostrar): 4950 -> "1,97". */
export const multiplierLabel = (chance: number) => (Math.floor((PAYOUT_NUMERATOR * 100) / chance) / 100).toFixed(2).replace(".", ",");

/** Si la tirada gana. Under: sale por debajo del limite; Over: sale en el tramo superior de ese mismo ancho. */
export const wins = (direction: DiceDirection, chance: number, roll: number) =>
  direction === "Under" ? roll < chance : roll >= DICE_ROLLS - chance;

/** El limite que se muestra: "menos de 49,50" o "50,50 o más". */
export const limitLabel = (direction: DiceDirection, chance: number) =>
  direction === "Under" ? `menos de ${formatHundredths(chance)}` : `${formatHundredths(DICE_ROLLS - chance)} o más`;
