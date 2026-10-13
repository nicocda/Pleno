import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { authMessage } from "../auth/messages";

/** Sin `token`: pide el email y manda el enlace. Con `token` (el del correo): elige la contraseña nueva. */
export function RecoverPassword() {
  const { client } = useAuth();
  const [params] = useSearchParams();
  const token = params.get("token");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending) return;
    setPending(true);
    setError(null);
    try {
      if (token) await client.resetPassword(token, password);
      else await client.forgotPassword(email.trim());
      setDone(true);
    } catch (failure) {
      setError(authMessage(failure));
    } finally {
      setPending(false);
    }
  };

  return (
    <main className="landing">
      <div className="landing__card">
        <h1 className="landing__title">Pleno</h1>
        {done ? (
          <>
            <p className="landing__info" role="status">
              {token
                ? "Listo: tu contraseña cambió y cerramos tus otras sesiones."
                : "Si hay una cuenta con ese email, te enviamos un correo con el enlace para elegir una contraseña nueva."}
            </p>
            <Link to="/" className="btn btn--gold">{token ? "Entrar" : "Volver"}</Link>
          </>
        ) : (
          <form className="auth-form" onSubmit={(event) => void submit(event)}>
            <p className="landing__lead">{token ? "Elegí tu contraseña nueva." : "Escribí tu email y te mandamos un enlace para recuperar tu cuenta."}</p>
            {token ? (
              <label className="field">
                <span>Contraseña nueva</span>
                <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" minLength={10} required />
              </label>
            ) : (
              <label className="field">
                <span>Email</span>
                <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
              </label>
            )}
            <button type="submit" className="btn btn--gold btn--lg" disabled={pending} aria-busy={pending}>
              {pending ? "Un momento…" : token ? "Cambiar contraseña" : "Enviar enlace"}
            </button>
            {error && <p className="landing__error" role="alert">{error}</p>}
            <Link to="/" className="landing__fine">Volver</Link>
          </form>
        )}
      </div>
    </main>
  );
}
