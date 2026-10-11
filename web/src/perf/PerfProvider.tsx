import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { PERF_STORAGE_KEY, readPerfChoice, resolvePerfMode, type PerfChoice, type PerfMode } from "./perf";

interface PerfApi {
  choice: PerfChoice;
  mode: PerfMode;
  setChoice: (choice: PerfChoice) => void;
}

const PerfContext = createContext<PerfApi | null>(null);

/** Deja `data-perf="full|lite"` en <html>: el CSS apaga blur, sombras animadas y confeti en modo ligero. */
export function PerfProvider({ children }: { children: ReactNode }) {
  const [choice, setChoiceState] = useState<PerfChoice>(readPerfChoice);
  const mode = useMemo(() => resolvePerfMode(choice), [choice]);

  // Antes de pintar: los canvas se crean en efectos de los hijos, que ya tienen que ver el modo correcto.
  // eslint-disable-next-line react-hooks/immutability
  if (typeof document !== "undefined") document.documentElement.dataset.perf = mode;
  useEffect(() => {
    document.documentElement.dataset.perf = mode;
  }, [mode]);

  const setChoice = useCallback((next: PerfChoice) => {
    setChoiceState(next);
    try {
      window.localStorage.setItem(PERF_STORAGE_KEY, next);
    } catch {
      /* sin almacenamiento: no se recuerda */
    }
  }, []);

  const value = useMemo(() => ({ choice, mode, setChoice }), [choice, mode, setChoice]);
  return <PerfContext.Provider value={value}>{children}</PerfContext.Provider>;
}

export function usePerf(): PerfApi {
  const context = useContext(PerfContext);
  if (!context) throw new Error("usePerf debe usarse dentro de <PerfProvider>.");
  return context;
}
