import { Link } from "react-router-dom";
import { BACKOFFICE_ROLE, useAdminWithdrawals, useMe, useResolveWithdrawal } from "../api/hooks";
import { useToasts } from "../components/Toasts";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";

const shortId = (id: string) => `${id.slice(0, 8)}…${id.slice(-4)}`;

/** Backoffice: los retiros de quien no tiene cajero (un jugador suelto o un jefe de cajeros). Cobrarlos devuelve las fichas a la casa; rechazarlos las devuelve al jugador. */
export function AdminWithdrawals() {
  const me = useMe();
  const isAdmin = me.data?.roles.includes(BACKOFFICE_ROLE) ?? false;
  const pending = useAdminWithdrawals(isAdmin);
  const resolve = useResolveWithdrawal("backoffice");
  const toasts = useToasts();

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!isAdmin) return <p className="notice notice--error" role="alert">No tenés permisos de administrador.</p>;

  return (
    <div className="stack">
      <section className="card" aria-labelledby="retiros-titulo">
        <p className="eyebrow">Administración</p>
        <h1 id="retiros-titulo" className="display">Retiros sin cajero</h1>
        <p className="muted">
          Los retiros de un jugador con cajero los atiende su cajero. Acá van los de quien no tiene: al cobrarlos las fichas vuelven a la casa. <Link to="/admin">Volver al panel</Link>
        </p>
      </section>

      <section className="card" aria-labelledby="retiros-lista">
        <h2 id="retiros-lista">Pendientes</h2>
        {pending.isError && <p className="notice notice--error" role="alert">No pudimos cargar los retiros.</p>}
        {pending.data?.length === 0 && <p className="muted">No hay retiros pendientes.</p>}
        <ul className="admin-users">
          {pending.data?.map((w) => (
            <li key={w.id} className="admin-user">
              <span className="admin-user__id">{w.playerName ?? shortId(w.playerUserId)}</span>
              <span>{formatChips(w.amount)} fichas</span>
              <span className="muted">{formatDateTime(w.createdAt)}</span>
              <span className="admin-quick">
                <button type="button" className="btn btn--gold" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: w.id, action: "pay" }, { onSuccess: () => toasts.show("win", "Retiro cobrado: las fichas volvieron a la casa."), onError: (e) => toasts.show("error", errorMessage(e)) })}>
                  Cobrar
                </button>
                <button type="button" className="btn btn--ghost" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: w.id, action: "reject" }, { onSuccess: () => toasts.show("info", "Retiro rechazado: las fichas volvieron al jugador."), onError: (e) => toasts.show("error", errorMessage(e)) })}>
                  Rechazar
                </button>
              </span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
