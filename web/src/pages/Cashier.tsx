import { useState, type FormEvent } from "react";
import { CASHIER_ROLE, HEAD_CASHIER_ROLE, isCashierRole, useCashierMe, useCashierMembers, useCashierTransfers, useCashierWithdrawals, useLoadChips, useMe, useResolveWithdrawal } from "../api/hooks";
import type { HierarchyLevel } from "../api/types";
import { useToasts } from "../components/Toasts";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";
import { useRealtime } from "../realtime/RealtimeProvider";

/** Tope por carga: evita un cero de mas al tipear. */
export const MAX_LOAD = 1_000_000;

const shortId = (id: string) => `${id.slice(0, 8)}…${id.slice(-4)}`;

/** Como se muestra a una persona: su nombre de usuario si se conoce (con el id abreviado aparte) o, si no, el id abreviado. */
const personName = (name: string | null | undefined, id: string) => name ?? shortId(id);

export const LEVEL_NAMES: Record<HierarchyLevel, string> = { player: "Jugador", cashier: "Cajero", head_cashier: "Jefe de cajeros" };

/** Cajero y jefe de cajeros: cargan fichas desde su PROPIO saldo a la gente de su jurisdiccion (el jefe a sus cajeros, el cajero a sus jugadores). */
export function Cashier() {
  const me = useMe();
  const allowed = isCashierRole(me.data?.roles);
  const place = useCashierMe(allowed);
  const members = useCashierMembers(allowed && place.isSuccess);
  const transfers = useCashierTransfers(allowed && place.isSuccess);
  const withdrawals = useCashierWithdrawals(allowed && place.isSuccess);
  const resolve = useResolveWithdrawal("cashier");
  const load = useLoadChips();
  const toasts = useToasts();
  const { balance } = useRealtime();
  const [selected, setSelected] = useState<string | null>(null);
  const [amount, setAmount] = useState("");
  // Una clave por intento: si el envio se repite (doble clic, reintento), el servidor no carga dos veces.
  const [attemptKey, setAttemptKey] = useState(() => crypto.randomUUID());

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!allowed) return <p className="notice notice--error" role="alert">No sos cajero ni jefe de cajeros.</p>;
  if (place.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (place.isError || !place.data) {
    return (
      <p className="notice notice--error" role="alert">
        Todavía no te asignaron un lugar en la jerarquía de cajeros. Pedile al administrador que te ubique.
      </p>
    );
  }

  const isHead = me.data?.roles.includes(HEAD_CASHIER_ROLE) ?? false;
  const level = isHead ? HEAD_CASHIER_ROLE : CASHIER_ROLE;
  const targetsName = isHead ? "cajeros" : "jugadores";
  const commissionPercent = (place.data.commissionPermille ?? 0) / 10;
  const value = Number(amount);
  const validAmount = Number.isInteger(value) && value > 0 && value <= MAX_LOAD;
  const enough = !balance.ready || value <= balance.available;
  const canSubmit = selected !== null && validAmount && enough && !load.isPending;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit || selected === null) return;
    load.mutate(
      { toUserId: selected, amount: value, idempotencyKey: attemptKey },
      {
        onSuccess: (result) => {
          toasts.show("win", `Cargaste ${formatChips(value)} fichas.${result.commission > 0 ? ` Ganaste ${formatChips(result.commission)} de comisión.` : ""}`);
          setAmount("");
          setAttemptKey(crypto.randomUUID());
        },
        onError: (error) => toasts.show("error", errorMessage(error)),
      },
    );
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="cajero-titulo">
        <p className="eyebrow">{LEVEL_NAMES[level]}</p>
        <h1 id="cajero-titulo" className="display">Cargar fichas a tu gente</h1>
        <p className="muted">
          Las fichas salen de <strong>tu saldo</strong> ({balance.ready ? `${formatChips(balance.available)} fichas` : "…"}): se mueven, no se crean. Podés cargarle a tus {targetsName}; si te quedás sin fichas,
          pedile al {isHead ? "administrador" : "jefe de cajeros"}.
          {commissionPercent > 0 && <> Por cada carga cobrás una <strong>comisión del {commissionPercent} %</strong> que paga la casa (con tope por carga y por día).</>}
        </p>
      </section>

      <div className="admin-grid">
        <section className="card" aria-labelledby="cajero-gente">
          <h2 id="cajero-gente">Tus {targetsName}</h2>
          {members.isLoading && <p className="muted" role="status">Cargando…</p>}
          {members.isError && <p className="notice notice--error" role="alert">No pudimos cargar tu gente.</p>}
          <ul className="admin-users">
            {members.data?.map((m) => (
              <li key={m.userId}>
                <button type="button" className={`admin-user ${selected === m.userId ? "admin-user--on" : ""}`} aria-pressed={selected === m.userId} onClick={() => setSelected(m.userId)}>
                  <span className="admin-user__id">{personName(m.displayName, m.userId)}</span>
                  {m.displayName && <span className="muted mono">{shortId(m.userId)}</span>}
                  <span className="muted">{m.available === null ? "sin cuenta todavía" : `${formatChips(m.available)} fichas`}</span>
                </button>
              </li>
            ))}
          </ul>
          {members.data?.length === 0 && <p className="muted">Todavía no tenés {targetsName} a cargo. El administrador te los asigna.</p>}
        </section>

        <section className="card" aria-labelledby="cajero-carga">
          <h2 id="cajero-carga">Carga</h2>
          {selected === null ? (
            <p className="muted">Elegí a quién cargarle.</p>
          ) : (
            <form onSubmit={submit} className="stack">
              <label className="field">
                <span>Fichas a cargar</span>
                <input type="number" inputMode="numeric" min={1} max={MAX_LOAD} step={1} value={amount} onChange={(e) => setAmount(e.target.value)} />
              </label>
              <div className="admin-quick">
                {[100, 500, 1_000].map((n) => (
                  <button key={n} type="button" className="btn btn--ghost" onClick={() => setAmount(String(n))}>
                    {formatChips(n)}
                  </button>
                ))}
              </div>
              {validAmount && !enough && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa carga.</p>}
              <button type="submit" className="btn btn--gold" disabled={!canSubmit}>
                {load.isPending ? "Cargando…" : "Cargar fichas"}
              </button>
            </form>
          )}
        </section>
      </div>

      <section className="card" aria-labelledby="cajero-retiros">
        <h2 id="cajero-retiros">Retiros pendientes</h2>
        <p className="muted">Quien quiere retirar aparta sus fichas y vos las cobrás: pasan a tu saldo. Si lo rechazás, le vuelven.</p>
        {withdrawals.data?.length === 0 && <p className="muted">No hay retiros pendientes.</p>}
        <ul className="admin-users">
          {withdrawals.data?.map((w) => (
            <li key={w.id} className="admin-user">
              <span className="admin-user__id">{personName(w.playerName, w.playerUserId)}</span>
              <span>{formatChips(w.amount)} fichas</span>
              <span className="admin-quick">
                <button type="button" className="btn btn--gold" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: w.id, action: "pay" }, { onSuccess: () => toasts.show("win", `Cobraste ${formatChips(w.amount)} fichas.`), onError: (e) => toasts.show("error", errorMessage(e)) })}>
                    Cobrar
                  </button>
                <button type="button" className="btn btn--ghost" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: w.id, action: "reject" }, { onSuccess: () => toasts.show("info", "Rechazaste el retiro: las fichas volvieron."), onError: (e) => toasts.show("error", errorMessage(e)) })}>
                  Rechazar
                </button>
              </span>
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="cajero-historial">
        <h2 id="cajero-historial">Tus últimas cargas</h2>
        {transfers.data?.length === 0 && <p className="muted">Todavía no cargaste a nadie.</p>}
        {transfers.data && transfers.data.length > 0 && (
          <div className="admin-audit-scroll">
            <table className="admin-audit">
              <thead>
                <tr><th>Cuándo</th><th>A</th><th className="num">Fichas</th></tr>
              </thead>
              <tbody>
                {transfers.data.map((t) => (
                  <tr key={t.transactionId}>
                    <td>{formatDateTime(t.occurredAt)}</td>
                    <td className={t.targetName ? undefined : "mono"}>{personName(t.targetName, t.targetUserId)}</td>
                    <td className="num">{formatChips(t.amount)}</td>
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
