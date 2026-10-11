import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "./AuthContext";

/** Cuanto se espera a Keycloak antes de avisar que no responde (si no contesta, el navegador no dice nada por mucho tiempo). */
const LOGIN_TIMEOUT_MS = 15_000;

export const LOGIN_ERROR_MESSAGE =
  "No pudimos conectar con el servidor de identidad (Keycloak). Si recién arrancó el sistema, esperá unos segundos y volvé a intentar.";

/**
 * Inicia sesion mostrando que pasa: mientras conecta con Keycloak el boton queda en "Conectando…", y si Keycloak todavia no esta
 * disponible (por ejemplo, el sistema recien arranca) se explica en vez de no hacer nada.
 */
export function useLogin(returnTo = "/") {
  const { login } = useAuth();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const start = useCallback(async () => {
    setPending(true);
    setError(null);
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const timeout = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("timeout")), LOGIN_TIMEOUT_MS);
      });
      // Si todo sale bien el navegador se va a Keycloak y esta pagina deja de existir.
      await Promise.race([login(returnTo), timeout]);
    } catch {
      if (mounted.current) setError(LOGIN_ERROR_MESSAGE);
    } finally {
      clearTimeout(timer);
      if (mounted.current) setPending(false);
    }
  }, [login, returnTo]);

  return { start, pending, error };
}
