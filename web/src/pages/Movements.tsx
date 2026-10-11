import { useGames, useMovements } from "../api/hooks";
import { BalanceChip } from "../components/BalanceChip";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";
import { movementLabel, signedChips } from "../lib/movements";

/** Extracto de la cuenta: cada cosa que subio o bajo tus fichas disponibles, con el saldo que quedo. */
export function Movements() {
  const movements = useMovements();
  const items = movements.data?.pages.flatMap((page) => page.items) ?? [];
  const games = useGames();
  // Si el catalogo no esta (todavia o fallo), se muestra el identificador tal cual: mejor eso que dejar la celda vacia.
  const gameName = (id: string | null | undefined) => (id ? (games.data?.find((g) => g.id === id)?.name ?? id) : "—");

  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Tu cuenta</p>
        <h1 className="display">Movimientos</h1>
        <p className="muted">
          Cada vez que cambian tus fichas disponibles: cargas, apuestas, premios y devoluciones. Una apuesta que se pierde se ve como la apuesta
          (las fichas ya salieron al apostar) y no tiene un segundo movimiento.
        </p>
      </header>

      <section className="card" aria-labelledby="mov-saldo">
        <h2 id="mov-saldo" className="section-title">Saldo actual</h2>
        <BalanceChip size="xl" />
      </section>

      <section className="card" aria-labelledby="mov-lista">
        <h2 id="mov-lista" className="section-title">Extracto</h2>
        {movements.isLoading && <p className="muted" role="status">Cargando movimientos…</p>}
        {movements.isError && <p className="notice notice--error" role="alert">{errorMessage(movements.error)}</p>}
        {movements.isSuccess && items.length === 0 && <p className="muted">Todavía no hay movimientos.</p>}

        {items.length > 0 && (
          <div className="admin-audit-scroll">
            <table className="admin-audit" aria-label="Movimientos de saldo">
              <thead>
                <tr><th>Cuándo</th><th>Concepto</th><th>Juego</th><th className="num">Fichas</th><th className="num">Saldo</th></tr>
              </thead>
              <tbody>
                {items.map((m) => (
                  <tr key={m.version}>
                    <td>{formatDateTime(m.at)}</td>
                    <td>{movementLabel(m.kind)}</td>
                    <td>{gameName(m.gameId)}</td>
                    <td className={`num ${m.delta > 0 ? "delta--up" : "delta--down"}`}>{signedChips(m.delta, formatChips)}</td>
                    <td className="num">{formatChips(m.balanceAfter)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {movements.hasNextPage && (
          <button type="button" className="btn btn--ghost" disabled={movements.isFetchingNextPage} onClick={() => void movements.fetchNextPage()}>
            {movements.isFetchingNextPage ? "Cargando…" : "Ver movimientos anteriores"}
          </button>
        )}
      </section>
    </div>
  );
}
