import { useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { BACKOFFICE_ROLE, isCashierRole, useMe } from "../api/hooks";
import { useAuth } from "../auth/AuthContext";
import { useRealtime, type ConnectionState } from "../realtime/RealtimeProvider";
import { useTheme } from "../theme/ThemeProvider";
import { BalanceChip } from "./BalanceChip";

const CONNECTION_LABEL: Record<ConnectionState, string> = {
  connecting: "Conectando…",
  connected: "En vivo",
  reconnecting: "Reconectando…",
  disconnected: "Sin conexión en vivo",
};

export function Layout() {
  const { displayName, logout } = useAuth();
  const { connection } = useRealtime();
  const { theme, toggle } = useTheme();
  const roles = useMe().data?.roles;
  const isAdmin = roles?.includes(BACKOFFICE_ROLE) ?? false;
  const isCashier = isCashierRole(roles);

  // En el celular la navegacion, el tema y el usuario viven en un menu desplegable para que la barra ocupe una sola fila fina.
  // Se guarda en que pagina se abrio: al navegar a otra, el menu queda cerrado solo.
  const { pathname } = useLocation();
  const [openedOn, setOpenedOn] = useState<string | null>(null);
  const menuOpen = openedOn === pathname;
  const header = useRef<HTMLElement>(null);
  useEffect(() => {
    if (!menuOpen) return;
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && setOpenedOn(null);
    const onPointer = (event: PointerEvent) => header.current && !header.current.contains(event.target as Node) && setOpenedOn(null);
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPointer);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onPointer);
    };
  }, [menuOpen]);

  return (
    <div className="shell">
      <a className="skip-link" href="#contenido">
        Saltar al contenido
      </a>
      <header className="topbar" ref={header}>
        <NavLink to="/" className="brand" aria-label="Pleno, ir al lobby">
          <span className="brand__chip" aria-hidden="true">P</span>
          <span className="brand__name">Pleno</span>
        </NavLink>

        <div className="topbar__status">
          <span className={`live live--${connection}`} title={CONNECTION_LABEL[connection]} role="status">
            <span className="live__dot" aria-hidden="true" />
            <span className="live__text">{CONNECTION_LABEL[connection]}</span>
          </span>
          <BalanceChip />
        </div>

        <button
          type="button"
          className="menu-btn"
          aria-label={menuOpen ? "Cerrar el menú" : "Abrir el menú"}
          aria-expanded={menuOpen}
          aria-controls="menu-principal"
          onClick={() => setOpenedOn(menuOpen ? null : pathname)}
        >
          <span aria-hidden="true">{menuOpen ? "✕" : "☰"}</span>
        </button>

        <div id="menu-principal" className={`topbar__menu${menuOpen ? " is-open" : ""}`}>
          <nav className="topbar__nav" aria-label="Principal">
            <NavLink to="/" end>Lobby</NavLink>
            <NavLink to="/ruleta">Ruleta</NavLink>
            <NavLink to="/historial">Historial</NavLink>
            <NavLink to="/movimientos">Movimientos</NavLink>
            <NavLink to="/retirar">Retirar</NavLink>
            {isCashier && <NavLink to="/cajero">Cajero</NavLink>}
            {isAdmin && <NavLink to="/admin">Admin</NavLink>}
          </nav>

          <div className="topbar__tools">
            <button type="button" className="icon-btn" onClick={toggle} aria-label={theme === "dark" ? "Cambiar a tema claro" : "Cambiar a tema oscuro"}>
              {theme === "dark" ? "☀" : "☾"}
            </button>
            <div className="user">
              <span className="user__name">{displayName ?? "Jugador"}</span>
              <button type="button" className="link-btn" onClick={() => void logout()}>
                Salir
              </button>
            </div>
          </div>
        </div>
      </header>

      <main id="contenido" className="content">
        <Outlet />
      </main>

      <footer className="footer">
        Pleno es un laboratorio: las fichas no tienen valor y no hay dinero real.
      </footer>
    </div>
  );
}
