import { Suspense } from "react";
import { Route, Routes } from "react-router-dom";
import { ApiProvider } from "./api/ApiProvider";
import { RequireAuth } from "./auth/RequireAuth";
import { GAME_PAGES } from "./games/registry";
import { Admin } from "./pages/Admin";
import { AdminCredits } from "./pages/AdminCredits";
import { AdminHierarchy } from "./pages/AdminHierarchy";
import { AdminSlots } from "./pages/AdminSlots";
import { AdminWithdrawals } from "./pages/AdminWithdrawals";
import { Cashier } from "./pages/Cashier";
import { Withdraw } from "./pages/Withdraw";
import { Movements } from "./pages/Movements";
import { AuthCallback } from "./pages/AuthCallback";
import { History } from "./pages/History";
import { Lobby } from "./pages/Lobby";
import { NotFound } from "./pages/NotFound";

/** Rutas. Los providers de sesion y tema viven en `main.tsx`; los datos del servidor, aca. */
export function App() {
  return (
    <ApiProvider>
      <Routes>
        <Route path="/auth/callback" element={<AuthCallback />} />
        <Route element={<RequireAuth />}>
          <Route index element={<Lobby />} />
          {/* Un juego = una pagina registrada (games/registry). Agregar o quitar uno no toca estas rutas. */}
          {GAME_PAGES.map(({ id, route, component: Game }) => (
            <Route key={id} path={route} element={<Suspense fallback={<p className="muted" role="status">Cargando el juego…</p>}><Game /></Suspense>} />
          ))}
          <Route path="historial" element={<History />} />
          <Route path="admin" element={<Admin />} />
          <Route path="admin/cargas" element={<AdminCredits />} />
          <Route path="admin/tragamonedas" element={<AdminSlots />} />
          <Route path="admin/jerarquia" element={<AdminHierarchy />} />
          <Route path="cajero" element={<Cashier />} />
          <Route path="retirar" element={<Withdraw />} />
          <Route path="admin/retiros" element={<AdminWithdrawals />} />
          <Route path="movimientos" element={<Movements />} />
        </Route>
        <Route path="*" element={<NotFound />} />
      </Routes>
    </ApiProvider>
  );
}
