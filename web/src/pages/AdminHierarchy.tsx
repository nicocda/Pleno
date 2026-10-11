import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { BACKOFFICE_ROLE, useAdminUsers, useAssignHierarchy, useHierarchy, useMe } from "../api/hooks";
import type { HierarchyLevel } from "../api/types";
import { useToasts } from "../components/Toasts";
import { errorMessage } from "../lib/messages";
import { LEVEL_NAMES } from "./Cashier";

const shortId = (id: string) => `${id.slice(0, 8)}…${id.slice(-4)}`;

/** El nivel del padre que corresponde a cada nivel: un jugador depende de un cajero y un cajero de un jefe; el jefe no tiene padre. */
const PARENT_LEVEL: Record<HierarchyLevel, HierarchyLevel | null> = { player: "cashier", cashier: "head_cashier", head_cashier: null };
const LEVELS: HierarchyLevel[] = ["head_cashier", "cashier", "player"];

/** Backoffice: arma el arbol de cargas (jefe de cajeros → cajero → jugador). Los roles de Keycloak dan acceso; este arbol dice a quien se le puede cargar. */
export function AdminHierarchy() {
  const me = useMe();
  const isAdmin = me.data?.roles.includes(BACKOFFICE_ROLE) ?? false;
  const users = useAdminUsers(isAdmin);
  const tree = useHierarchy(isAdmin);
  const assign = useAssignHierarchy();
  const toasts = useToasts();
  const [userId, setUserId] = useState("");
  const [level, setLevel] = useState<HierarchyLevel>("cashier");
  const [parentUserId, setParentUserId] = useState("");

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!isAdmin) return <p className="notice notice--error" role="alert">No tenés permisos de administrador.</p>;

  const nodes = tree.data ?? [];
  const parentLevel = PARENT_LEVEL[level];
  const parents = parentLevel === null ? [] : nodes.filter((n) => n.level === parentLevel);
  const canSubmit = userId !== "" && !assign.isPending && (parentLevel === null || parentUserId !== "");

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit) return;
    assign.mutate(
      { userId, level, parentUserId: parentLevel === null ? null : parentUserId },
      {
        onSuccess: () => toasts.show("win", `Quedó como ${LEVEL_NAMES[level].toLowerCase()}.`),
        onError: (error) => toasts.show("error", errorMessage(error)),
      },
    );
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="jerarquia-titulo">
        <p className="eyebrow">Administración</p>
        <h1 id="jerarquia-titulo" className="display">Cajeros y jerarquía</h1>
        <p className="muted">
          El jefe de cajeros recibe fichas del administrador y se las carga a sus cajeros; cada cajero las carga a sus jugadores. Ubicá acá a cada uno y de quién depende. Además, la persona tiene que
          tener el rol <code>cashier</code> o <code>head_cashier</code> en Keycloak. <Link to="/admin">Volver al panel</Link>
        </p>
      </section>

      <div className="admin-grid">
        <section className="card" aria-labelledby="jerarquia-arbol">
          <h2 id="jerarquia-arbol">El árbol</h2>
          {tree.isError && <p className="notice notice--error" role="alert">No pudimos cargar la jerarquía.</p>}
          {nodes.length === 0 && !tree.isLoading && <p className="muted">Todavía no hay nadie ubicado.</p>}
          {LEVELS.map((lvl) => {
            const group = nodes.filter((n) => n.level === lvl);
            if (group.length === 0) return null;
            return (
              <div key={lvl}>
                <h3 className="section-subtitle">{LEVEL_NAMES[lvl]}{lvl === "head_cashier" ? "" : "s"}</h3>
                <ul className="admin-users">
                  {group.map((n) => (
                    <li key={n.userId} className="admin-user">
                      <span className="admin-user__id">{n.displayName ?? shortId(n.userId)}</span>
                      {n.displayName && <span className="muted mono">{shortId(n.userId)}</span>}
                      <span className="muted">{n.parentUserId ? `depende de ${shortId(n.parentUserId)}` : "sin padre"}</span>
                    </li>
                  ))}
                </ul>
              </div>
            );
          })}
        </section>

        <section className="card" aria-labelledby="jerarquia-ubicar">
          <h2 id="jerarquia-ubicar">Ubicar a alguien</h2>
          <form onSubmit={submit} className="stack">
            <label className="field">
              <span>Persona</span>
              <select value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">Elegí…</option>
                {users.data?.map((u) => (
                  <option key={u.userId} value={u.userId}>{shortId(u.userId)}</option>
                ))}
              </select>
            </label>
            <label className="field">
              <span>Nivel</span>
              <select value={level} onChange={(e) => { setLevel(e.target.value as HierarchyLevel); setParentUserId(""); }}>
                {LEVELS.map((lvl) => (
                  <option key={lvl} value={lvl}>{LEVEL_NAMES[lvl]}</option>
                ))}
              </select>
            </label>
            {parentLevel !== null && (
              <label className="field">
                <span>Depende de ({LEVEL_NAMES[parentLevel].toLowerCase()})</span>
                <select value={parentUserId} onChange={(e) => setParentUserId(e.target.value)}>
                  <option value="">Elegí…</option>
                  {parents.map((p) => (
                    <option key={p.userId} value={p.userId}>{p.displayName ? `${p.displayName} (${shortId(p.userId)})` : shortId(p.userId)}</option>
                  ))}
                </select>
              </label>
            )}
            {parentLevel !== null && parents.length === 0 && <p className="muted">Primero ubicá a un {LEVEL_NAMES[parentLevel].toLowerCase()}.</p>}
            <button type="submit" className="btn btn--gold" disabled={!canSubmit}>
              {assign.isPending ? "Guardando…" : "Guardar"}
            </button>
          </form>
        </section>
      </div>
    </div>
  );
}
