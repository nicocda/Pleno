import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { AuthError } from "../auth/identity";
import { authMessage, RETURN_TO_KEY } from "../auth/messages";

type Mode = "login" | "register";

/** Pantalla para quien todavia no inicio sesion: entrar, crear cuenta o entrar con Google. */
export function Landing() {
  const { signIn, client } = useAuth();
  const [mode, setMode] = useState<Mode>("login");
  const [google, setGoogle] = useState(false);
  const [login, setLogin] = useState("");
  const [alias, setAlias] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [needsVerification, setNeedsVerification] = useState(false);
  const [info, setInfo] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void client.providers().then((p) => {
      if (active) setGoogle(p.google);
    });
    return () => {
      active = false;
    };
  }, [client]);

  const switchMode = (next: Mode) => {
    setMode(next);
    setError(null);
    setInfo(null);
    setNeedsVerification(false);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending) return;
    setPending(true);
    setError(null);
    setInfo(null);
    setNeedsVerification(false);
    try {
      if (mode === "login") {
        await signIn(login.trim(), password);
      } else {
        await client.register(alias.trim(), email.trim(), password);
        // Siempre el mismo aviso: no se revela si el email ya tenia cuenta.
        setInfo("Te enviamos un correo para confirmar tu email. Cuando lo confirmes vas a poder entrar.");
        setMode("login");
        setLogin(alias.trim());
        setPassword("");
      }
    } catch (failure) {
      setError(authMessage(failure));
      setNeedsVerification(failure instanceof AuthError && failure.code === "email_not_verified");
    } finally {
      setPending(false);
    }
  };

  const resend = async () => {
    const address = login.includes("@") ? login.trim() : email.trim();
    if (!address.includes("@")) {
      setError("Escribí tu email en «Alias o email» para reenviarte el correo.");
      return;
    }
    try {
      await client.resendVerification(address);
      setInfo("Si hay una cuenta sin verificar con ese email, te enviamos el correo de nuevo.");
      setError(null);
    } catch (failure) {
      setError(authMessage(failure));
    }
  };

  const withGoogle = () => {
    try {
      sessionStorage.setItem(RETURN_TO_KEY, window.location.pathname + window.location.search);
    } catch {
      // Sin almacenamiento se vuelve al inicio.
    }
    window.location.assign(client.googleStartUrl(`${window.location.origin}/auth/callback`));
  };

  return (
    <main className="landing">
      <div className="landing__card">
        <span className="brand__chip brand__chip--lg" aria-hidden="true">P</span>
        <h1 className="landing__title">Pleno</h1>
        <p className="landing__lead">El casino de fichas sin riesgo: jugá, comprobá cada resultado y mirá tu saldo moverse en vivo.</p>

        <div className="auth-tabs" role="tablist" aria-label="Acceso">
          <button type="button" role="tab" aria-selected={mode === "login"} className={`btn ${mode === "login" ? "btn--gold" : "btn--ghost"}`} onClick={() => switchMode("login")}>
            Entrar
          </button>
          <button type="button" role="tab" aria-selected={mode === "register"} className={`btn ${mode === "register" ? "btn--gold" : "btn--ghost"}`} onClick={() => switchMode("register")}>
            Crear cuenta
          </button>
        </div>

        <form className="auth-form" onSubmit={(event) => void submit(event)}>
          {mode === "login" ? (
            <label className="field">
              <span>Alias o email</span>
              <input value={login} onChange={(e) => setLogin(e.target.value)} autoComplete="username" required />
            </label>
          ) : (
            <>
              <label className="field">
                <span>Alias</span>
                <input value={alias} onChange={(e) => setAlias(e.target.value)} autoComplete="nickname" minLength={3} maxLength={20} required />
              </label>
              <label className="field">
                <span>Email</span>
                <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
              </label>
            </>
          )}
          <label className="field">
            <span>Contraseña</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete={mode === "login" ? "current-password" : "new-password"}
              minLength={mode === "register" ? 10 : undefined}
              required
            />
          </label>
          <button type="submit" className="btn btn--gold btn--lg" disabled={pending} aria-busy={pending}>
            {pending ? "Un momento…" : mode === "login" ? "Entrar" : "Crear cuenta"}
          </button>
        </form>

        {error && <p className="landing__error" role="alert">{error}</p>}
        {needsVerification && (
          <button type="button" className="btn btn--ghost" onClick={() => void resend()}>
            Reenviar el correo de verificación
          </button>
        )}
        {info && <p className="landing__info" role="status">{info}</p>}

        {google && (
          <button type="button" className="btn btn--ghost btn--lg" onClick={withGoogle}>
            Entrar con Google
          </button>
        )}
        {mode === "login" && <Link to="/recuperar" className="landing__fine">¿Olvidaste tu contraseña?</Link>}

        <ul className="landing__points">
          <li><strong>Sin dinero real.</strong> Las fichas son ficticias.</li>
          <li><strong>Resultados verificables.</strong> Podés recalcular cada tirada vos mismo.</li>
          <li><strong>Saldo exacto.</strong> Cada ficha queda registrada y se puede auditar.</li>
        </ul>
        <p className="landing__fine">Tu email se guarda cifrado y solo se usa para verificar y recuperar tu cuenta. Los demás jugadores solo ven tu alias.</p>
      </div>
    </main>
  );
}
