import { useState } from "react";
import { Link } from "react-router-dom";
import { BACKOFFICE_ROLE, CASHIER_ROLE, HEAD_CASHIER_ROLE, useIdentityUsers, useMe, useSetRoles } from "../api/hooks";
import type { IdentityUser } from "../api/types";
import { useToasts } from "../components/Toasts";
import { formatDateTime } from "../lib/format";

/** Roles que se pueden dar. `player` lo tiene todo el mundo y no se toca. */
const ASSIGNABLE: { role: string; label: string }[] = [
  { role: CASHIER_ROLE, label: "Cajero" },
  { role: HEAD_CASHIER_ROLE, label: "Jefe de cajeros" },
  { role: BACKOFFICE_ROLE, label: "Administrador" },
];

function RoleRow({ user, isSelf }: { user: IdentityUser; isSelf: boolean }) {
  const setRoles = useSetRoles();
  const toasts = useToasts();
  const [draft, setDraft] = useState<string[]>(() => user.roles.filter((r) => ASSIGNABLE.some((a) => a.role === r)));
  const original = user.roles.filter((r) => ASSIGNABLE.some((a) => a.role === r));
  const changed = draft.length !== original.length || draft.some((r) => !original.includes(r));

  const toggle = (role: string) => setDraft((current) => (current.includes(role) ? current.filter((r) => r !== role) : [...current, role]));

  const save = () =>
    setRoles.mutate(
      { id: user.id, roles: draft },
      {
        onSuccess: () => toasts.show("win", `Roles de ${user.alias} actualizados. Sus sesiones se cerraron.`),
        onError: () => toasts.show("error", "No se pudieron guardar los roles."),
      },
    );

  return (
    <li className="admin-user">
      <span className="admin-user__id">{user.alias}</span>
      {isSelf && <span className="admin-user__me">vos</span>}
      <span className="muted">Alta {formatDateTime(user.createdAt)}{user.emailConfirmed ? "" : " · email sin verificar"}</span>
      <span className="admin-roles">
        {ASSIGNABLE.map(({ role, label }) => (
          <label key={role} className="admin-roles__option">
            <input
              type="checkbox"
              checked={draft.includes(role)}
              disabled={isSelf && role === BACKOFFICE_ROLE}
              onChange={() => toggle(role)}
              aria-label={`${label} para ${user.alias}`}
            />
            {label}
          </label>
        ))}
      </span>
      <button type="button" className="btn btn--gold" disabled={!changed || setRoles.isPending} onClick={save}>
        {setRoles.isPending ? "Guardando…" : "Guardar"}
      </button>
    </li>
  );
}

/** Backoffice: da o quita roles a las cuentas. Reemplaza a la consola de administracion del servidor de identidad anterior. */
export function AdminRoles() {
  const me = useMe();
  const isAdmin = me.data?.roles.includes(BACKOFFICE_ROLE) ?? false;
  const [query, setQuery] = useState("");
  const users = useIdentityUsers(query.trim(), isAdmin);

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!isAdmin) return <p className="notice notice--error" role="alert">No tenés permisos de administrador.</p>;

  return (
    <div className="stack">
      <section className="card" aria-labelledby="roles-titulo">
        <p className="eyebrow">Administración</p>
        <h1 id="roles-titulo" className="display">Roles de las cuentas</h1>
        <p className="muted">
          Buscá una cuenta por su alias y dale (o quitale) un rol. Todas las cuentas son jugadores. Al guardar se cierran las sesiones abiertas de esa cuenta para que el cambio valga enseguida.
          Para que alguien reparta fichas también hay que ubicarlo en <Link to="/admin/jerarquia">la jerarquía</Link>. <Link to="/admin">Volver al panel</Link>
        </p>
        <label className="field">
          <span>Alias (empieza con…)</span>
          <input value={query} onChange={(e) => setQuery(e.target.value)} autoComplete="off" />
        </label>
      </section>

      <section className="card" aria-labelledby="roles-lista">
        <h2 id="roles-lista">Cuentas</h2>
        {users.isLoading && <p className="muted" role="status">Cargando cuentas…</p>}
        {users.isError && <p className="notice notice--error" role="alert">No pudimos cargar las cuentas.</p>}
        {users.data?.length === 0 && <p className="muted">No hay cuentas con ese alias.</p>}
        <ul className="admin-users">
          {users.data?.map((u) => (
            <RoleRow key={`${u.id}:${u.roles.join(",")}`} user={u} isSelf={u.id === me.data?.userId} />
          ))}
        </ul>
      </section>
    </div>
  );
}
