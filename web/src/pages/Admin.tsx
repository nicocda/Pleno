import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { BACKOFFICE_ROLE, useAdminAccount, useAdminAudit, useAdminUsers, useCreditChips, useMe } from "../api/hooks";
import { useToasts } from "../components/Toasts";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";

/** Tope por carga en el panel: evita un cero de mas al tipear. */
export const MAX_CREDIT = 1_000_000;

const shortId = (id: string) => `${id.slice(0, 8)}…${id.slice(-4)}`;

/** Panel de administracion: elegir un jugador y cargarle fichas. Solo para quien tenga el rol `backoffice`. */
export function Admin() {
  const me = useMe();
  const isAdmin = me.data?.roles.includes(BACKOFFICE_ROLE) ?? false;
  const users = useAdminUsers(isAdmin);
  const credit = useCreditChips();
  const toasts = useToasts();
  const [selected, setSelected] = useState<string | null>(null);
  const [amount, setAmount] = useState("");
  // Una clave por intento: si el envio se repite (doble clic, reintento), el servidor no acredita dos veces.
  const [attemptKey, setAttemptKey] = useState(() => crypto.randomUUID());
  const account = useAdminAccount(selected);
  const audit = useAdminAudit(isAdmin);

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!isAdmin) {
    return (
      <p className="notice notice--error" role="alert">
        No tenés permisos de administrador.
      </p>
    );
  }

  const value = Number(amount);
  const validAmount = Number.isInteger(value) && value > 0 && value <= MAX_CREDIT;
  const canSubmit = selected !== null && validAmount && !credit.isPending;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit || selected === null) return;
    credit.mutate(
      { userId: selected, amount: value, idempotencyKey: attemptKey },
      {
        onSuccess: () => {
          toasts.show("win", `Se cargaron ${formatChips(value)} fichas.`);
          setAmount("");
          setAttemptKey(crypto.randomUUID());
        },
        onError: (error) => toasts.show("error", errorMessage(error)),
      },
    );
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="admin-titulo">
        <p className="eyebrow">Administración</p>
        <h1 id="admin-titulo" className="display">Cargar fichas</h1>
        <p className="muted">Elegí un jugador y cargale fichas. Cada carga queda como un asiento en su cuenta.</p>
        <p className="muted">Otras secciones: <Link to="/admin/tragamonedas">ajustes de la tragamonedas</Link> · <Link to="/admin/cargas">historial de cargas</Link> · <Link to="/admin/jerarquia">cajeros y jerarquía</Link> · <Link to="/admin/retiros">retiros sin cajero</Link></p>
      </section>

      <div className="admin-grid">
        <section className="card" aria-labelledby="admin-jugadores">
          <h2 id="admin-jugadores">Jugadores</h2>
          {users.isLoading && <p className="muted" role="status">Cargando jugadores…</p>}
          {users.isError && <p className="notice notice--error" role="alert">No pudimos cargar los jugadores.</p>}
          <ul className="admin-users">
            {users.data?.map((u) => (
              <li key={u.userId}>
                <button
                  type="button"
                  className={`admin-user ${selected === u.userId ? "admin-user--on" : ""}`}
                  aria-pressed={selected === u.userId}
                  onClick={() => setSelected(u.userId)}
                >
                  <span className="admin-user__id">{shortId(u.userId)}</span>
                  {u.userId === me.data?.userId && <span className="admin-user__me">vos</span>}
                  <span className="muted">Alta {formatDateTime(u.registeredAt)}</span>
                </button>
              </li>
            ))}
          </ul>
          {users.data?.length === 0 && <p className="muted">Todavía no hay jugadores.</p>}
        </section>

        <section className="card" aria-labelledby="admin-carga">
          <h2 id="admin-carga">Carga</h2>
          {selected === null ? (
            <p className="muted">Elegí un jugador de la lista.</p>
          ) : (
            <form onSubmit={submit} className="stack">
              <p>
                Saldo actual:{" "}
                <strong>{account.data ? `${formatChips(account.data.available)} fichas` : account.isError ? "sin cuenta todavía" : "…"}</strong>
              </p>
              <label className="field">
                <span>Fichas a cargar</span>
                <input
                  type="number"
                  inputMode="numeric"
                  min={1}
                  max={MAX_CREDIT}
                  step={1}
                  value={amount}
                  onChange={(e) => setAmount(e.target.value)}
                />
              </label>
              <div className="admin-quick">
                {[100, 1_000, 10_000].map((n) => (
                  <button key={n} type="button" className="btn btn--ghost" onClick={() => setAmount(String(n))}>
                    {formatChips(n)}
                  </button>
                ))}
              </div>
              <button type="submit" className="btn btn--gold" disabled={!canSubmit}>
                {credit.isPending ? "Cargando…" : "Cargar fichas"}
              </button>
            </form>
          )}
        </section>
      </div>

      <section className="card" aria-labelledby="admin-auditoria">
        <h2 id="admin-auditoria">Auditoría</h2>
        <p className="muted">Las últimas cargas. Solo se agrega: no se edita ni se borra. <Link to="/admin/cargas">Ver el historial completo con filtros</Link></p>
        {audit.isError && <p className="notice notice--error" role="alert">No pudimos cargar la auditoría.</p>}
        {audit.data?.length === 0 && <p className="muted">Todavía no hay cargas.</p>}
        {audit.data && audit.data.length > 0 && (
          <div className="admin-audit-scroll">
            <table className="admin-audit">
              <thead>
                <tr><th>Cuándo</th><th>Administrador</th><th>Jugador</th><th className="num">Fichas</th></tr>
              </thead>
              <tbody>
                {audit.data.map((e) => (
                  <tr key={e.transactionId}>
                    <td>{formatDateTime(e.occurredAt)}</td>
                    <td className="mono">{shortId(e.actorUserId)}{e.actorUserId === me.data?.userId && " (vos)"}</td>
                    <td className="mono">{shortId(e.targetUserId)}</td>
                    <td className="num">+{formatChips(e.amount)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
