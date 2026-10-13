import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { authMessage } from "../auth/messages";

type State = { kind: "working" } | { kind: "done" } | { kind: "failed"; message: string };

/** Destino del enlace del correo de bienvenida: confirma el email. */
export function VerifyEmail() {
  const { client } = useAuth();
  const [params] = useSearchParams();
  const token = params.get("token");
  const [state, setState] = useState<State>(token ? { kind: "working" } : { kind: "failed", message: authMessage("invalid_token") });
  // El enlace es de un solo uso: React en desarrollo monta los efectos dos veces.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (!token) return;
    client
      .verifyEmail(token)
      .then(() => setState({ kind: "done" }))
      .catch((error: unknown) => setState({ kind: "failed", message: authMessage(error) }));
  }, [client, token]);

  return (
    <main className="landing">
      <div className="landing__card" role={state.kind === "failed" ? "alert" : "status"}>
        <h1 className="landing__title">Pleno</h1>
        {state.kind === "working" && <p className="landing__lead">Confirmando tu email…</p>}
        {state.kind === "done" && (
          <>
            <p className="landing__lead">¡Listo! Tu email quedó confirmado.</p>
            <Link to="/" className="btn btn--gold btn--lg">Entrar</Link>
          </>
        )}
        {state.kind === "failed" && (
          <>
            <p className="landing__error">{state.message}</p>
            <Link to="/" className="btn btn--ghost">Volver</Link>
          </>
        )}
      </div>
    </main>
  );
}
