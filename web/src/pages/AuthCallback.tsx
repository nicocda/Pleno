import { useEffect, useRef, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { authMessage, RETURN_TO_KEY } from "../auth/messages";

/** Aca vuelve el login con Google: el servicio de identidad deja un codigo de un solo uso que se cambia por la sesion. */
export function AuthCallback() {
  const { signInWithCode } = useAuth();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const code = params.get("code");
  const [failure, setFailure] = useState<string | null>(code ? null : authMessage("invalid_code"));
  // El codigo se puede canjear una sola vez: React en desarrollo monta los efectos dos veces.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (!code) return;
    signInWithCode(code)
      .then(() => {
        let returnTo = "/";
        try {
          returnTo = sessionStorage.getItem(RETURN_TO_KEY) ?? "/";
          sessionStorage.removeItem(RETURN_TO_KEY);
        } catch {
          // Sin almacenamiento se vuelve al inicio.
        }
        // Solo rutas propias: nunca se vuelve a otro sitio.
        navigate(returnTo.startsWith("/") && !returnTo.startsWith("//") ? returnTo : "/", { replace: true });
      })
      .catch((error: unknown) => setFailure(authMessage(error)));
  }, [code, signInWithCode, navigate]);

  if (failure) {
    return (
      <main className="landing">
        <div className="landing__card" role="alert">
          <h1 className="landing__title">No pudimos iniciar tu sesión</h1>
          <p className="landing__lead">{failure}</p>
          <Link to="/" className="btn btn--gold">Volver a intentar</Link>
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
