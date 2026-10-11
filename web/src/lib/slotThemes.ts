import { useState } from "react";

/** Un estilo visual de la tragamonedas. El servidor sortea los mismos simbolos; el tema solo decide como se ven. */
export type SlotThemeId = "clasico" | "gemas";

export interface SlotTheme {
  id: SlotThemeId;
  name: string;
}

export const SLOT_THEMES: readonly SlotTheme[] = [
  { id: "gemas", name: "Gemas" },
  { id: "clasico", name: "Clásico" },
];

/** Nombre del simbolo en cada tema (lo que lee el lector de pantalla y la tabla de pagos). Si no hay, se usa el del servidor. */
const GEM_LABELS: Record<string, string> = { Cereza: "Rubí", Limon: "Topacio", Naranja: "Citrino", Campana: "Esmeralda", Bar: "Zafiro", Siete: "Diamante" };
export const themedLabel = (theme: SlotThemeId, name: string, fallback: string): string => (theme === "gemas" ? (GEM_LABELS[name] ?? fallback) : fallback);

const KEY = "pleno.slots.theme";
const isTheme = (value: unknown): value is SlotThemeId => SLOT_THEMES.some((t) => t.id === value);

/** El estilo elegido se recuerda en este navegador (si no se puede guardar, simplemente no se recuerda). */
export function useSlotTheme(): [SlotThemeId, (id: SlotThemeId) => void] {
  const [theme, setTheme] = useState<SlotThemeId>(() => {
    try {
      const saved = window.localStorage.getItem(KEY);
      return isTheme(saved) ? saved : "gemas";
    } catch {
      return "gemas";
    }
  });
  return [
    theme,
    (id) => {
      setTheme(id);
      try {
        window.localStorage.setItem(KEY, id);
      } catch {
        /* preferencia cosmetica: no pasa nada si no se guarda */
      }
    },
  ];
}
