import { Link } from "react-router-dom";
import { useAccount, useGames, useMe, useRounds } from "../api/hooks";
import { BalanceChip } from "../components/BalanceChip";
import { COMING_SOON, lobbyGames, pageFor } from "../games/registry";
import { RoundBadge } from "../components/RoundBadge";
import { formatChips, formatDateTime } from "../lib/format";
import { describeRound } from "../lib/roulette";
import { outcomeText } from "../lib/messages";
import { useRealtime } from "../realtime/RealtimeProvider";

export function Lobby() {
  const me = useMe();
  const account = useAccount();
  const rounds = useRounds(5);
  const catalog = useGames();
  const { balance } = useRealtime();

  // Los juegos disponibles salen del catalogo del servidor; los que todavia no existen se muestran como "Proximamente".
  const available = lobbyGames(catalog.data ?? []);
  const soon = COMING_SOON.filter((game) => !available.some((a) => a.id === game.id));

  return (
    <div className="stack">
      <section className="hero card" aria-labelledby="saludo">
        <div>
          <p className="eyebrow">Bienvenido</p>
          <h1 id="saludo" className="display">Hola, {me.data?.displayName ?? "jugador"}</h1>
          {balance.reserved > 0 && <p className="muted">{formatChips(balance.reserved)} fichas en juego ahora mismo.</p>}
        </div>
        <BalanceChip size="xl" />
      </section>

      {account.isLoading && <p className="notice" role="status">Estamos preparando tu cuenta…</p>}
      {account.isError && (
        <p className="notice notice--error" role="alert">
          No pudimos abrir tu cuenta todavía.{" "}
          <button type="button" className="link-btn" onClick={() => void account.refetch()}>Reintentar</button>
        </p>
      )}

      <section aria-labelledby="juegos">
        <h2 id="juegos" className="section-title">Juegos</h2>
        <div className="games">
          {catalog.isLoading && <p className="muted" role="status">Cargando los juegos…</p>}
          {available.map((game) =>
            pageFor(game.id) ? (
              <Link key={game.id} to={game.route} className="game game--open">
                <span className="game__glyph" aria-hidden="true">{game.glyph}</span>
                <span className="game__name">{game.name}</span>
                <span className="game__tag">{game.tagline}</span>
                <span className="game__cta">Jugar →</span>
              </Link>
            ) : (
              // El servidor tiene el juego pero este front todavia no sabe como mostrarlo.
              <div key={game.id} className="game game--soon" aria-disabled="true">
                <span className="game__glyph" aria-hidden="true">{game.glyph}</span>
                <span className="game__name">{game.name}</span>
                <span className="game__tag">{game.tagline}</span>
                <span className="game__cta">Próximamente</span>
              </div>
            ),
          )}
          {soon.map((game) => (
            <div key={game.id} className="game game--soon" aria-disabled="true">
              <span className="game__glyph" aria-hidden="true">{game.glyph}</span>
              <span className="game__name">{game.name}</span>
              <span className="game__tag">{game.tagline}</span>
              <span className="game__cta">Próximamente</span>
            </div>
          ))}
          {catalog.isError && <p className="notice notice--error" role="alert">No pudimos cargar los juegos.</p>}
        </div>
      </section>

      <section aria-labelledby="ultimas" className="card">
        <div className="card__head">
          <h2 id="ultimas" className="section-title">Últimas jugadas</h2>
          <Link to="/historial" className="link-btn">Ver todo</Link>
        </div>
        {rounds.isLoading && <p className="muted">Cargando…</p>}
        {rounds.data?.length === 0 && <p className="muted">Todavía no jugaste. Probá la ruleta.</p>}
        <ul className="list">
          {rounds.data?.map((round) => (
            <li key={round.betId} className="list__row">
              <span>
                <strong>{describeRound(round)}</strong>
                <span className="muted"> · {formatChips(round.stake)} · {formatDateTime(round.placedAt)}</span>
              </span>
              <span className="list__end">
                <RoundBadge status={round.status} />
                <span>{outcomeText(round)}</span>
              </span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
