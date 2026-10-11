import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLogin } from "../auth/useLogin";

/** Aca vuelve Keycloak despues del login: se canjea el codigo por el token y se regresa a donde estaba el jugador. */
export function AuthCallback() {
  const { completeLogin } = useAuth();
  const { start, pending, error } = useLogin("/");
  const navigate = useNavigate();
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    completeLogin()
      .then((returnTo) => active && navigate(returnTo, { replace: true }))
      .catch(() => active && setFailed(true));
    return () => {
      active = false;
    };
  }, [completeLogin, navigate]);

  if (failed) {
    return (
      <main className="landing">
        <div className="landing__card" role="alert">
          <h1 className="landing__title">No pudimos iniciar tu sesión</h1>
          <p className="landing__lead">El inicio de sesión se interrumpió. Intentá de nuevo.</p>
          <button type="button" className="btn btn--gold" onClick={() => void start()} disabled={pending} aria-busy={pending}>
            {pending ? "Conectando…" : "Reintentar"}
          </button>
          {error && <p className="landing__error">{error}</p>}
        </div>
      </main>
    );
  }

  return (
    <main className="landing">
      <p className="splash" role="status">Iniciando sesión…</p>
    </main>
  );
}
