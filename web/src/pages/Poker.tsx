import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { queryKeys, useTableAction } from "../api/hooks";
import type { PokerAction, PokerPlayer, PokerView, TableSeat } from "../api/types";
import { Confetti } from "../components/Confetti";
import { TablesLobby, type LiveTable } from "../components/TablesLobby";
import { sha256Hex } from "../lib/crash";
import { formatChips } from "../lib/format";
import { detailedErrorMessage } from "../lib/messages";
import { clampRaise, decodeCard, eventText, handName, raiseSize } from "../lib/poker";
import { playWinSound } from "../lib/winSound";

const GAME_ID = "poker";

/** Poker (Texas Hold'em, una mano por mesa): mesas entre jugadores (y bots), cada uno entra con la misma entrada y al final cobra lo que le queda. */
export function Poker() {
  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Poker</p>
        <h1 className="display">Texas Hold'em</h1>
        <p className="muted">Armá una mesa de 2 a 6 jugadores (o con bots), cada uno entra con la misma entrada y se juega una mano: ciegas automáticas, cuatro rondas de apuestas y showdown. El mazo se baraja con una semilla comprometida de antemano y se revela al terminar.</p>
      </header>
      <TablesLobby<PokerView> gameId={GAME_ID} gameName="Poker" renderGame={(table, leave) => <PokerTable table={table} onLeave={leave} />} />
      <Rules />
    </div>
  );
}

function Rules() {
  return (
    <section className="card" aria-labelledby="pk-reglas">
      <h2 id="pk-reglas" className="section-title">Reglas</h2>
      <ul className="pk-rules">
        <li>Cada asiento entra con la misma entrada (de 20 a 2.000 fichas). Las ciegas son automáticas: la chica es la entrada dividida 50 (mínimo 1) y la grande, el doble.</li>
        <li>Recibís 2 cartas privadas y se muestran hasta 5 comunitarias (flop, turn y river). Armás tu mejor mano de 5 entre tus 2 cartas y las de la mesa.</li>
        <li>Límite libre: podés subir hasta todo lo que tenés. Si no te alcanza para subir pero sí para igualar, igualar es all-in.</li>
        <li>Hay 4 rondas de apuestas (preflop, flop, turn y river) y un solo pozo. Si todos se retiran menos uno, se lleva el pozo sin mostrar cartas; si no, hay showdown.</li>
        <li>Al terminar cobrás lo que te queda de tu entrada: tu ganancia neta es lo que cobrás menos lo que pusiste.</li>
        <li>Cada turno dura 30 segundos. Si no actuás, pasás (si es gratis) o te retirás; con 3 turnos vencidos seguidos un bot juega por vos. No se puede salir en plena partida.</li>
      </ul>
    </section>
  );
}

/** Una carta con CSS: sin imagenes. `card` null es una carta boca abajo. */
function PokerCard({ card, size = "md", label, win }: { card: number | null; size?: "sm" | "md" | "lg"; label?: string; win?: boolean }) {
  if (card === null) return <span className={`pk-card pk-card--${size} pk-card--back`} role="img" aria-label={label ?? "Carta boca abajo"} />;
  const info = decodeCard(card);
  const classes = ["pk-card", `pk-card--${size}`, info.red ? "pk-card--red" : "", win ? "pk-card--win" : ""].filter(Boolean).join(" ");
  return (
    <span className={classes} role="img" aria-label={label ?? info.label}>
      <span className="pk-card__rank" aria-hidden="true">{info.rankSymbol}</span>
      <span className="pk-card__suit" aria-hidden="true">{info.symbol}</span>
    </span>
  );
}

const secondsUntil = (iso: string | null, serverNowMs: number): number => (iso === null ? 0 : Math.max(0, Math.ceil((Date.parse(iso) - serverNowMs) / 1000)));

type Verification = { ok: boolean } | null;

function PokerTable({ table, onLeave }: { table: LiveTable<PokerView>; onLeave: () => void }) {
  const queryClient = useQueryClient();
  const act = useTableAction<PokerAction>(GAME_ID, table.id);
  const game = table.game;
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [sawPlaying, setSawPlaying] = useState(false);
  const [winClosed, setWinClosed] = useState(false);
  const [verification, setVerification] = useState<Verification>(null);

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(id);
  }, []);

  const finished = table.status === "Finished";
  const mySeat = table.mySeat;
  const payout = mySeat !== null ? table.payouts?.[mySeat] ?? null : null;
  const net = payout === null ? null : payout - table.buyIn;

  // Se festeja solo una victoria que se vio resolverse en esta pantalla (no una vieja que quedo del estado al entrar).
  if (table.status === "Playing" && !sawPlaying) setSawPlaying(true);
  const celebrate = finished && sawPlaying && net !== null && net > 0 && !winClosed;
  const justFinished = finished && sawPlaying;
  useEffect(() => {
    if (justFinished) void queryClient.invalidateQueries({ queryKey: queryKeys.account });
  }, [justFinished, queryClient]);
  useEffect(() => {
    if (celebrate) playWinSound();
  }, [celebrate]);

  const nameOf = (seat: number): string => table.seats.find((s) => s.seat === seat)?.name ?? `Asiento ${seat + 1}`;
  const serverNow = now + table.offsetMs;

  if (!game) return <p className="muted" role="status">Repartiendo las cartas…</p>;

  const over = finished || game.done;
  const myTurn = !over && mySeat !== null && game.current === mySeat;
  const turnSeconds = secondsUntil(table.turnEndsAt, serverNow);
  const me = game.players.find((p) => p.seat === mySeat);
  const players = [...game.players].sort((a, b) => a.seat - b.seat);
  const winners = game.showdown.length > 0 ? game.showdown.filter((s) => s.won).map((s) => s.seat) : game.winnings.flatMap((w, seat) => (w > 0 ? [seat] : []));
  const winnerEntry = game.showdown.find((s) => s.won);
  const winnerText = winners.length === 0 ? "" : `Ganó ${winners.map(nameOf).join(" y ")}${winnerEntry ? ` con ${handName(winnerEntry, game.board)}` : ""}`;

  const send = (action: PokerAction) => {
    setError(null);
    act.mutate(action, { onError: (e) => setError(detailedErrorMessage(e)) });
  };

  const statusLabel = over ? (winnerText || "La mano terminó")
    : mySeat === null ? `Mirando la mano: juega ${nameOf(game.current)}`
    : myTurn ? `Es tu turno (${turnSeconds} s)${game.toCall > 0 ? `: igualá ${formatChips(Math.min(game.toCall, me?.stack ?? game.toCall))} o subí` : ": pasá o apostá"}`
    : `Juega ${nameOf(game.current)}${table.seats.find((s) => s.seat === game.current)?.away ? " (ausente: lo juega un bot)" : ""}`;

  const proof = async () => {
    if (table.serverSeed === null) return;
    setVerification({ ok: (await sha256Hex(table.serverSeed)) === table.commitment.toLowerCase() });
  };

  const board = [...game.board, ...Array<null>(Math.max(0, 5 - game.board.length)).fill(null)];

  return (
    <div className="stack">
      <section className="card" aria-labelledby="pk-mesa">
        <div className="pk-head">
          <h2 id="pk-mesa" className="section-title">{table.name}</h2>
          <p className="muted">Entrada de {formatChips(table.buyIn)} fichas · ciegas {formatChips(Math.max(1, Math.floor(table.buyIn / 50)))} y {formatChips(Math.max(1, Math.floor(table.buyIn / 50)) * 2)}</p>
        </div>
        <p role="status" className="crash-status" data-testid="pk-status">{statusLabel}</p>

        <div className="pk-felt">
          <div className="pk-center">
            <div className="pk-board" role="group" aria-label="Cartas comunitarias" data-testid="pk-board">
              {board.map((c, i) => (c === null ? <span key={i} className="pk-card pk-card--md pk-card--empty" aria-hidden="true" /> : <PokerCard key={i} card={c} />))}
            </div>
            <p className="pk-pot" data-testid="pk-pot">Pozo: <strong>{formatChips(game.pot)}</strong> <span className="pk-street">{streetName(game.street)}</span></p>
          </div>

          <ul className="pk-players" aria-label="Jugadores">
            {players.map((p) => (
              <PlayerView key={p.seat} player={p} game={game} seat={table.seats.find((s) => s.seat === p.seat)} name={nameOf(p.seat)} mine={p.seat === mySeat} active={!over && game.current === p.seat} turnSeconds={turnSeconds} over={over} board={game.board} />
            ))}
          </ul>

          <div className="pk-dock dock">
          {mySeat !== null && (
            <div className="pk-mine">
              <p className="bj-who">Tus cartas</p>
              <div className="pk-hand" role="group" aria-label="Tus cartas" data-testid="pk-hand">
                {game.hand.map((c) => (
                  <PokerCard key={c} card={c} size="lg" />
                ))}
                {game.hand.length === 0 && <span className="muted">Sin cartas</span>}
              </div>
            </div>
          )}

          {myTurn && me && <ActionBar key={`${game.street}-${game.currentBet}-${game.pot}`} game={game} stack={me.stack} bet={me.bet} pending={act.isPending} onAct={send} />}
          </div>
          {error && <p className="notice notice--error" role="alert">{error}</p>}
        </div>

        {over && (
          <div role="status" className="pk-result" data-testid="pk-result">
            {net !== null && (
              <p>
                <strong>{net > 0 ? `¡Ganaste! +${formatChips(net)} fichas` : net < 0 ? `Perdiste ${formatChips(-net)} fichas` : "Recuperaste tu entrada"}</strong>
              </p>
            )}
            {winnerText && <p>{winnerText}.</p>}
            {payout !== null && <p className="muted">Pusiste {formatChips(table.buyIn)} y cobraste {formatChips(payout)}: {net! >= 0 ? "ganancia" : "pérdida"} neta de {formatChips(Math.abs(net!))} fichas.</p>}
            <button type="button" className="btn btn--gold" onClick={onLeave}>Volver a las mesas</button>
          </div>
        )}
      </section>

      {over && game.showdown.length > 0 && (
        <section className="card" aria-labelledby="pk-showdown">
          <h2 id="pk-showdown" className="section-title">Showdown</h2>
          <ul className="pk-showdown" aria-label="Manos reveladas">
            {game.showdown.map((s) => (
              <li key={s.seat} className={s.won ? "pk-showdown__row pk-showdown__row--won" : "pk-showdown__row"} data-testid="pk-showdown-row">
                <span className="pk-showdown__name">{nameOf(s.seat)}</span>
                <span className="pk-showdown__cards">{s.cards.map((c) => <PokerCard key={c} card={c} size="sm" />)}</span>
                <span>{handName(s, game.board)}</span>
                {s.won && <strong className="pk-tag pk-tag--win">Ganadora · +{formatChips(game.winnings[s.seat] ?? 0)}</strong>}
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="card" aria-labelledby="pk-hist">
        <h2 id="pk-hist" className="section-title">Lo último que pasó</h2>
        <ol className="pk-events" role="log" aria-label="Historial de la mano">
          {[...game.events].reverse().map((event, i) => (
            <li key={`${game.events.length - i}-${event.kind}-${event.seat}`}>{eventText(event, nameOf, game.board, game.showdown)}</li>
          ))}
          {game.events.length === 0 && <li className="muted">Todavía no jugó nadie.</li>}
        </ol>
      </section>

      <section className="card" aria-labelledby="pk-verificar">
        <h2 id="pk-verificar" className="section-title">Verificá la partida</h2>
        <p className="muted">Antes de repartir el servidor publicó el compromiso (el hash de su semilla). Al terminar la mano revela la semilla: si su hash coincide con el compromiso, el mazo no se cambió durante el juego.</p>
        <dl className="crash-proof">
          <dt>Compromiso</dt>
          <dd className="mono">{table.commitment}</dd>
          {table.serverSeed !== null && (
            <>
              <dt>Semilla revelada</dt>
              <dd className="mono">{table.serverSeed}</dd>
            </>
          )}
        </dl>
        {table.serverSeed !== null ? (
          <>
            <button type="button" className="btn btn--ghost" onClick={() => void proof()}>Verificar en mi navegador</button>
            {verification && (
              <p role="status" className={verification.ok ? "notice" : "notice notice--error"}>
                {verification.ok ? "✓ La semilla coincide con el compromiso." : "✗ La semilla NO coincide con el compromiso."}
              </p>
            )}
          </>
        ) : (
          <p className="muted">La semilla se revela cuando termina la mano.</p>
        )}
      </section>

      {celebrate && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWinClosed(true)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <p className="crash-multiplier crash-multiplier--running" aria-hidden="true">♠</p>
            <h2 id="win-title" className="win-modal__title">¡Ganaste!</h2>
            <p className="win-modal__amount">+{formatChips(net)} fichas</p>
            <p className="muted">Pusiste {formatChips(table.buyIn)} y cobraste {formatChips(payout ?? 0)}.</p>
            <button type="button" className="btn btn--gold" autoFocus onClick={() => setWinClosed(true)}>
              Continuar
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

const streetName = (street: PokerView["street"]): string =>
  ({ preflop: "Preflop", flop: "Flop", turn: "Turn", river: "River", done: "Mano terminada" })[street];

/** Retirarme / Pasar o Igualar / Subir (slider + campo + atajos). Se vuelve a montar cuando cambia la ronda, la apuesta o el pozo (el campo vuelve al minimo). */
function ActionBar({ game, stack, bet, pending, onAct }: { game: PokerView; stack: number; bet: number; pending: boolean; onAct: (action: PokerAction) => void }) {
  const canRaise = game.actions.includes("raise");
  const canCheck = game.actions.includes("check");
  const canCall = game.actions.includes("call");
  const { minRaiseTo, maxRaiseTo } = game;
  const [text, setText] = useState<string | null>(null);
  const amount = clampRaise(text === null ? minRaiseTo : Number(text), minRaiseTo, maxRaiseTo);
  const callAmount = Math.min(game.toCall, stack);
  const sizes = { pot: game.pot, myBet: bet, toCall: game.toCall, minRaiseTo, maxRaiseTo };

  return (
    <div className="pk-actions" role="group" aria-label="Tus jugadas" data-testid="pk-actions">
      <div className="pk-actions__main">
        <button type="button" className="btn btn--ghost btn--lg" disabled={pending || !game.actions.includes("fold")} onClick={() => onAct({ type: "fold" })}>Retirarme</button>
        {canCheck && <button type="button" className="btn btn--gold btn--lg" disabled={pending} onClick={() => onAct({ type: "check" })}>Pasar</button>}
        {canCall && (
          <button type="button" className="btn btn--gold btn--lg" disabled={pending} aria-label={`Igualar ${formatChips(callAmount)}`} onClick={() => onAct({ type: "call" })}>
            Igualar {formatChips(callAmount)}{callAmount >= stack ? " (all-in)" : ""}
          </button>
        )}
      </div>
      <fieldset className="pk-raise" disabled={!canRaise || pending}>
        <legend className="bj-who">Subir</legend>
        <div className="pk-raise__row">
          <input type="range" aria-label="Monto de la subida" min={Math.min(minRaiseTo, maxRaiseTo)} max={maxRaiseTo} step={1} value={amount} onChange={(e) => setText(e.target.value)} />
          <input type="number" inputMode="numeric" className="pk-raise__input" aria-label="Subir a" min={Math.min(minRaiseTo, maxRaiseTo)} max={maxRaiseTo} value={text ?? String(amount)} onChange={(e) => setText(e.target.value)} onBlur={() => setText(String(amount))} />
        </div>
        <div className="pk-raise__row">
          <button type="button" className="btn btn--ghost" onClick={() => setText(String(raiseSize("half", sizes)))}>½ pozo</button>
          <button type="button" className="btn btn--ghost" onClick={() => setText(String(raiseSize("pot", sizes)))}>Pozo</button>
          <button type="button" className="btn btn--ghost" onClick={() => setText(String(raiseSize("allin", sizes)))}>All-in</button>
          <button type="button" className="btn btn--gold" onClick={() => onAct({ type: "raise", to: amount })}>
            {amount >= maxRaiseTo ? `All-in ${formatChips(amount)}` : `Subir a ${formatChips(amount)}`}
          </button>
        </div>
      </fieldset>
    </div>
  );
}

function PlayerView({ player, game, seat, name, mine, active, turnSeconds, over, board }: { player: PokerPlayer; game: PokerView; seat: TableSeat | undefined; name: string; mine: boolean; active: boolean; turnSeconds: number; over: boolean; board: number[] }) {
  const entry = game.showdown.find((s) => s.seat === player.seat);
  const classes = ["pk-player", mine ? "pk-player--mine" : "", active ? "pk-player--active" : "", player.folded ? "pk-player--folded" : "", entry?.won ? "pk-player--winner" : ""].filter(Boolean).join(" ");
  const shown = !mine ? player.cards : null; // las propias se ven grandes abajo
  const status = player.folded ? "Retirado" : player.allIn ? "All-in" : active ? `Turno (${turnSeconds} s)` : "";
  return (
    <li className={classes} aria-current={active ? "true" : undefined} data-testid="pk-player">
      <p className="pk-player__name">
        {name}
        {mine ? " (vos)" : ""}
        {player.seat === game.dealer && <span className="pk-tag" aria-label="Botón del repartidor">D</span>}
        {player.seat === game.smallBlindSeat && <span className="pk-tag" aria-label="Ciega chica">SB</span>}
        {player.seat === game.bigBlindSeat && <span className="pk-tag" aria-label="Ciega grande">BB</span>}
        {seat?.isBot && <span className="pk-tag pk-tag--bot">Bot</span>}
      </p>
      <p className="pk-player__stack">Pila: <strong>{formatChips(player.stack)}</strong></p>
      {player.bet > 0 && !over && <p className="pk-player__bet" aria-label={`${name} apostó ${formatChips(player.bet)} en esta ronda`}><span className="pk-chip" aria-hidden="true" />{formatChips(player.bet)}</p>}
      {status && <p className={`pk-player__state${player.allIn ? " pk-player__state--allin" : ""}`}>{status}</p>}
      {seat?.away && !seat.isBot && !over && <p className="pk-player__state">Ausente: lo juega un bot</p>}
      {!mine && (
        <div className="pk-player__cards">
          {shown
            ? shown.map((c) => <PokerCard key={c} card={c} size="sm" win={entry?.won} />)
            : Array.from({ length: player.cardCount }, (_, i) => <PokerCard key={i} card={null} size="sm" label={`Carta boca abajo de ${name}`} />)}
        </div>
      )}
      {over && entry && <p className="pk-player__hand">{handName(entry, board)}</p>}
    </li>
  );
}
