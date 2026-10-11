import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { ApiError } from "../api/client";
import { queryKeys, useBlackjackAction, useBlackjackTable, useBlackjackTables, usePlaceBlackjackBet } from "../api/hooks";
import type { BlackjackRound, BlackjackSeat, BlackjackTable } from "../api/types";
import { Confetti } from "../components/Confetti";
import { StakePicker } from "../components/StakePicker";
import { cardFace, describeHand, handText, type HandVerification, isWin, resultText, verifyHand } from "../lib/blackjack";
import { formatChips } from "../lib/format";
import { errorMessage } from "../lib/messages";
import { playWinSound } from "../lib/winSound";
import { useRealtime } from "../realtime/RealtimeProvider";

const CHIPS = [10, 50, 100, 500, 1_000];

/** Blackjack: mesas compartidas contra el crupier. Elegis una mesa, apostas para sentarte y jugas tu turno (pedir o plantarte). */
export function Blackjack() {
  const [tableId, setTableId] = useState<string | null>(null);
  return tableId === null ? <TableList onPick={setTableId} /> : <TableView key={tableId} tableId={tableId} onLeave={() => setTableId(null)} />;
}

const phaseLabel = (phase: BlackjackTable["phase"], players: number): string =>
  phase === "Betting" ? "Apuestas abiertas"
  : phase === "Playing" ? "Repartiendo"
  : phase === "Finished" ? "Mano terminada"
  : phase === "Aborted" ? "Mano cancelada"
  : players === 0 ? "Esperando jugadores" : "Esperando la primera apuesta";

function Header({ children }: { children?: React.ReactNode }) {
  return (
    <header>
      <p className="eyebrow">Blackjack</p>
      <h1 className="display">Ganale al crupier</h1>
      <p className="muted">Sentate en una mesa, apostá y llegá a 21 sin pasarte. Cada mano se compromete con un hash antes de repartir y se puede verificar.</p>
      {children}
    </header>
  );
}

function Rules() {
  return (
    <section className="card" aria-labelledby="reglas">
      <h2 id="reglas" className="section-title">Reglas</h2>
      <ul className="bj-rules">
        <li>El crupier planta en todos los 17 (también en un 17 blando).</li>
        <li>Blackjack (as y figura) paga 3 a 2; ganar paga 1 a 1; el empate devuelve la apuesta.</li>
        <li>Por ahora no se puede doblar, dividir ni asegurar.</li>
        <li>El mazo es de 6 barajas, barajado con una semilla que se revela al terminar la mano.</li>
        <li>Tenés unos segundos por turno: si no jugás, la mano se planta sola.</li>
      </ul>
    </section>
  );
}

function TableList({ onPick }: { onPick: (id: string) => void }) {
  const tables = useBlackjackTables();
  return (
    <div className="stack">
      <Header />
      <section aria-labelledby="mesas">
        <h2 id="mesas" className="section-title">Mesas</h2>
        {tables.isLoading && <p className="muted" role="status">Cargando las mesas…</p>}
        {tables.isError && <p className="notice notice--error" role="alert">No pudimos cargar las mesas.</p>}
        <div className="games">
          {tables.data?.map((table) => (
            <button key={table.id} type="button" className="game game--open bj-table-card" onClick={() => onPick(table.id)}>
              <span className="game__glyph" aria-hidden="true">♠</span>
              <span className="game__name">{table.name}</span>
              <span className="game__tag">Apuesta de {formatChips(table.minStake)} a {formatChips(table.maxStake)} fichas</span>
              <span className="game__tag">{table.players}/{table.maxSeats} jugadores · {phaseLabel(table.phase, table.players)}</span>
              <span className="game__cta">Sentarme →</span>
            </button>
          ))}
        </div>
      </section>
      <Rules />
    </div>
  );
}

/** Una carta con CSS: sin imagenes. */
function PlayingCard({ card }: { card: number | "hidden" }) {
  if (card === "hidden") return <span className="bj-card bj-card--back" role="img" aria-label="Carta tapada" />;
  const face = cardFace(card);
  return (
    <span className={`bj-card ${face.red ? "bj-card--red" : ""}`} role="img" aria-label={face.label}>
      <span aria-hidden="true">{face.rank}</span>
      <span aria-hidden="true">{face.symbol}</span>
    </span>
  );
}

const secondsUntil = (iso: string | null, serverNowMs: number): number => (iso === null ? 0 : Math.max(0, Math.ceil((Date.parse(iso) - serverNowMs) / 1000)));

/** El mensaje de un error al sentarse o jugar; el 409 de "no se puede apostar ahora" se explica mejor que el generico. */
function tableError(e: unknown): string {
  if (e instanceof ApiError && e.status === 409 && e.title === "BettingClosed") return "La mesa está repartiendo o está llena. Esperá a la próxima mano.";
  if (e instanceof ApiError && e.status === 400 && e.title === "InvalidBet") return "Ese monto no está permitido en esta mesa.";
  return errorMessage(e);
}

function TableView({ tableId, onLeave }: { tableId: string; onLeave: () => void }) {
  const state = useBlackjackTable(tableId);
  const { balance, onGameEvent } = useRealtime();
  const queryClient = useQueryClient();
  const placeBet = usePlaceBlackjackBet(tableId);
  const act = useBlackjackAction();

  const data = state.data;
  const table = data?.table ?? null;
  const round = data?.round ?? null;
  const seats = data?.seats ?? [];
  const mine = seats.find((s) => s.mine) ?? null;

  const [stakeInput, setStakeInput] = useState<number | null>(null);
  const [autoRepeat, setAutoRepeat] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [win, setWin] = useState<BlackjackSeat | null>(null);
  const [lastFinished, setLastFinished] = useState<{ round: BlackjackRound; seats: BlackjackSeat[] } | null>(null);
  const [verification, setVerification] = useState<{ roundId: string; result: HandVerification | null } | null>(null);
  const pending = useRef<{ fingerprint: string; key: string } | null>(null);
  const watched = useRef(new Set<string>());

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(id);
  }, []);

  // Cada cambio de la mesa (repartio, jugo alguien, termino) refresca el estado al instante; la consulta cada segundo es solo respaldo.
  useEffect(
    () =>
      onGameEvent((event) => {
        if (event.game !== "blackjack") return;
        if (eventTableId(event.data) === tableId) void queryClient.invalidateQueries({ queryKey: queryKeys.blackjackTable(tableId) });
      }),
    [onGameEvent, queryClient, tableId],
  );

  // Se guarda la ultima mano terminada para poder verificarla aunque ya se haya abierto la siguiente.
  if (round?.phase === "Finished" && round.serverSeed !== null && lastFinished?.round.id !== round.id) setLastFinished({ round, seats });

  // Se festeja solo una victoria que se vio resolverse en esta pantalla (no una vieja que quedo del estado al entrar).
  useEffect(() => {
    if (!mine?.betId) return;
    if (mine.result === null) {
      watched.current.add(mine.betId);
    } else if (isWin(mine.result) && mine.payout !== null && watched.current.delete(mine.betId)) {
      setWin(mine);
      playWinSound();
      void queryClient.invalidateQueries({ queryKey: queryKeys.account });
    }
  }, [mine, queryClient]);

  const serverNow = now + (data?.offsetMs ?? 0);
  const phase = round?.phase ?? null;
  const min = table?.minStake ?? 1;
  const max = table?.maxStake ?? 10_000;
  const stake = stakeInput ?? Math.min(max, Math.max(min, 10));
  const stakeValid = Number.isInteger(stake) && stake >= min && stake <= max;
  const bettingOpen = (phase === null || phase === "Betting") && (round?.bettingEndsAt == null || serverNow < Date.parse(round.bettingEndsAt));
  const seated = mine !== null && round !== null;
  const canBet = bettingOpen && !seated && stakeValid && stake <= balance.available && !placeBet.isPending;
  const myTurn = phase === "Playing" && mine !== null && mine.seat !== null && round?.activeSeat === mine.seat && mine.betId !== null;
  const turnSeconds = secondsUntil(round?.turnEndsAt ?? null, serverNow);

  const submit = async () => {
    setError(null);
    const fingerprint = `${stake}`;
    if (pending.current?.fingerprint !== fingerprint) pending.current = { fingerprint, key: crypto.randomUUID() };
    try {
      await placeBet.mutateAsync({ stake, idempotencyKey: pending.current.key });
      pending.current = null; // aceptada: la proxima apuesta usa otra clave
    } catch (e) {
      setError(tableError(e));
      setAutoRepeat(false); // ante un error no se sigue apostando solo
      // Error definitivo: proxima apuesta con clave nueva. Si fue de red, se conserva para reintentar sin duplicar.
      if (e instanceof ApiError && e.status !== 0) pending.current = null;
    } finally {
      void queryClient.invalidateQueries({ queryKey: queryKeys.blackjackTable(tableId) });
    }
  };

  // Apuesta repetida: apenas se abre la mano siguiente se repite la apuesta anterior (se corta sola si falla o no alcanzan las fichas).
  // Una sola vez por mano (aunque el estado tarde en mostrarte sentado, no se apuesta dos veces).
  const submitRef = useRef(submit);
  const autoBetHand = useRef<string | null>(null);
  const handId = round?.id ?? "none";
  useEffect(() => {
    submitRef.current = submit;
  });
  useEffect(() => {
    if (!autoRepeat || !canBet || autoBetHand.current === handId) return;
    const id = window.setTimeout(() => {
      autoBetHand.current = handId;
      void submitRef.current();
    }, 0);
    return () => window.clearTimeout(id);
  }, [autoRepeat, canBet, handId]);

  const play = (action: "hit" | "stand") => {
    if (!mine?.betId) return;
    setError(null);
    act.mutate(
      { betId: mine.betId, action },
      {
        onError: (e) => setError(tableError(e)),
        onSettled: () => void queryClient.invalidateQueries({ queryKey: queryKeys.blackjackTable(tableId) }),
      },
    );
  };

  const statusLabel = !round ? "Esperando la primera apuesta"
    : phase === "Betting" ? (round.bettingEndsAt === null ? "Esperando la primera apuesta" : `Apuestas abiertas: cierran en ${secondsUntil(round.bettingEndsAt, serverNow)} s`)
    : phase === "Playing" ? (myTurn ? `Es tu turno: pedí o plantate (${turnSeconds} s)` : round.activeSeat !== null ? `Juega el asiento ${round.activeSeat}` : "Juega el crupier…")
    : phase === "Finished" ? "Mano terminada: la próxima empieza en unos segundos"
    : "La mano se canceló";

  const dealerCards = round?.dealer.cards ?? [];
  const hidden = round?.dealer.hiddenCards ?? 0;

  return (
    <div className="stack">
      <Header>
        <button type="button" className="link-btn" onClick={onLeave}>← Cambiar de mesa</button>
      </Header>

      {state.isError && <p className="notice notice--error" role="alert">No pudimos cargar la mesa.</p>}

      <div className="table-grid">
        <section className="card" aria-labelledby="mesa">
          <h2 id="mesa" className="section-title">{table?.name ?? "La mesa"}</h2>
          <p role="status" className="crash-status" data-testid="bj-status">{statusLabel}</p>

          <div className="bj-felt">
            <div className="bj-dealer" aria-label="Crupier">
              <p className="bj-who">Crupier{dealerCards.length > 0 && hidden === 0 ? ` · ${describeHand(dealerCards)}` : ""}</p>
              <div className="bj-hand">
                {dealerCards.map((card, i) => <PlayingCard key={i} card={card} />)}
                {Array.from({ length: hidden }, (_, i) => <PlayingCard key={`h${i}`} card="hidden" />)}
                {dealerCards.length === 0 && hidden === 0 && <span className="muted">Sin cartas</span>}
              </div>
            </div>

            <ul className="bj-seats" aria-label="Asientos">
              {seats.map((seat, i) => (
                <SeatView key={seat.seat ?? `p${i}`} seat={seat} position={i + 1} active={phase === "Playing" && seat.seat !== null && seat.seat === round?.activeSeat} turnSeconds={turnSeconds} />
              ))}
              {seats.length === 0 && <li className="muted">Todavía no se sentó nadie.</li>}
            </ul>
          </div>
        </section>

        <section className="card" aria-labelledby="apuesta">
          <h2 id="apuesta" className="section-title">Tu jugada</h2>

          {myTurn ? (
            <div className="bj-actions dock">
              <button type="button" className="btn btn--gold btn--lg" disabled={act.isPending} onClick={() => play("hit")}>Pedir</button>
              <button type="button" className="btn btn--ghost btn--lg" disabled={act.isPending} onClick={() => play("stand")}>Plantarme</button>
            </div>
          ) : seated ? (
            <SeatedNote seat={mine} />
          ) : (
            <>
              <p className="muted">Apuesta de {formatChips(min)} a {formatChips(max)} fichas.</p>
              <StakePicker stake={stake} onChange={setStakeInput} chips={CHIPS} min={min} max={max} defaultStake={Math.min(max, Math.max(min, 10))} disabled={!bettingOpen} />
              {!stakeValid && <p className="notice notice--error" role="alert">En esta mesa la apuesta va de {formatChips(min)} a {formatChips(max)} fichas.</p>}
              {stakeValid && stake > balance.available && balance.ready && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa apuesta.</p>}
              <label className="tl-check">
                <input type="checkbox" checked={autoRepeat} onChange={(e) => setAutoRepeat(e.target.checked)} />
                <span>Repetir la apuesta en cada mano</span>
              </label>
              <div className="dock">
                <button type="button" className="btn btn--gold btn--lg btn--block" disabled={!canBet} onClick={() => void submit()}>
                  {placeBet.isPending ? "Enviando…" : "Sentarme y apostar"}
                </button>
              </div>
              {!bettingOpen && round && <p className="muted" role="status">La mesa está repartiendo: podés sentarte en la próxima mano.</p>}
            </>
          )}
          {error && <p className="notice notice--error" role="alert">{error}</p>}
        </section>
      </div>

      <Rules />

      <section className="card" aria-labelledby="verificar">
        <h2 id="verificar" className="section-title">Verificá la última mano</h2>
        {lastFinished ? (
          <>
            <p className="muted">
              Antes de repartir el servidor publicó el compromiso (el hash de su semilla). Al terminar la mano reveló la semilla: con ella podés recalcular el mazo y comprobar que las cartas del reparto salieron de ahí.
            </p>
            <dl className="crash-proof">
              <dt>Compromiso</dt>
              <dd className="mono">{lastFinished.round.commitment}</dd>
              <dt>Semilla revelada</dt>
              <dd className="mono">{lastFinished.round.serverSeed}</dd>
            </dl>
            <button type="button" className="btn btn--ghost" onClick={() => void verifyHand(lastFinished.round, lastFinished.seats).then((result) => setVerification({ roundId: lastFinished.round.id, result }))}>
              Verificar en mi navegador
            </button>
            {verification?.roundId === lastFinished.round.id && verification.result && (
              <p role="status" className={verification.result.commitmentOk && verification.result.dealtOk ? "notice" : "notice notice--error"}>
                {verification.result.commitmentOk ? "✓ La semilla coincide con el compromiso." : "✗ La semilla NO coincide con el compromiso."}{" "}
                {verification.result.dealtOk ? "✓ Las cartas del reparto coinciden con el mazo recalculado." : "✗ Las cartas del reparto NO coinciden con el mazo recalculado."}
              </p>
            )}
          </>
        ) : (
          <p className="muted">Cuando termine la primera mano vas a poder verificarla acá.</p>
        )}
      </section>

      {win && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWin(null)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <p className="crash-multiplier crash-multiplier--running" aria-hidden="true">{win.result === "Blackjack" ? "21" : "♠"}</p>
            <h2 id="win-title" className="win-modal__title">¡Ganaste!</h2>
            <p className="win-modal__amount">{formatChips((win.payout ?? 0) - win.stake)} fichas</p>
            <p className="muted">Apostaste {formatChips(win.stake)} y cobraste {formatChips(win.payout ?? 0)}.</p>
            <button type="button" className="btn btn--gold" autoFocus onClick={() => setWin(null)}>
              Continuar
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function eventTableId(data: unknown): string | null {
  try {
    const parsed = (typeof data === "string" ? JSON.parse(data) : data) as { tableId?: unknown } | null;
    return typeof parsed?.tableId === "string" ? parsed.tableId : null;
  } catch {
    return null;
  }
}

function SeatedNote({ seat }: { seat: BlackjackSeat }) {
  if (seat.result !== null) {
    return (
      <div role="status">
        <p><strong>{resultText(seat.result)}</strong></p>
        {seat.payout !== null && <p className="muted">Apostaste {formatChips(seat.stake)} y cobraste {formatChips(seat.payout)} fichas.</p>}
        <p className="muted">En la próxima mano podés volver a apostar.</p>
      </div>
    );
  }
  if (seat.status === "Rejected") return <p className="notice notice--error" role="alert">No pudimos reservar tus fichas: la apuesta no se jugó.</p>;
  if (seat.seat === null) return <p role="status">Tu apuesta de {formatChips(seat.stake)} fichas está en la mesa. Esperá el reparto.</p>;
  return <p role="status">Tenés {formatChips(seat.stake)} fichas en juego. Esperá tu turno.</p>;
}

function SeatView({ seat, position, active, turnSeconds }: { seat: BlackjackSeat; position: number; active: boolean; turnSeconds: number }) {
  const title = seat.seat !== null ? `Asiento ${seat.seat}` : `Apuesta ${position}`;
  const classes = ["bj-seat", seat.mine ? "bj-seat--mine" : "", active ? "bj-seat--active" : "", seat.result && !isWin(seat.result) && seat.result !== "Push" ? "bj-seat--lost" : ""].join(" ");
  return (
    <li className={classes} data-testid={seat.mine ? "bj-seat-mine" : undefined} aria-current={active ? "true" : undefined}>
      <p className="bj-who">
        {title}{seat.mine ? " · vos" : ""}{active ? ` · turno (${turnSeconds} s)` : ""}
      </p>
      <div className="bj-hand">
        {seat.cards.map((card, i) => <PlayingCard key={i} card={card} />)}
        {seat.cards.length === 0 && <span className="muted">Esperando el reparto</span>}
      </div>
      <p className="bj-total">
        {seat.cards.length > 0 ? describeHand(seat.cards) : ""}
        {seat.cards.length > 0 ? " · " : ""}{formatChips(seat.stake)} fichas
      </p>
      {seat.result ? <p className={`bj-result ${isWin(seat.result) ? "win" : "muted"}`}>{resultText(seat.result)}</p> : seat.cards.length > 0 && <p className="muted bj-result">{handText(seat.hand)}</p>}
    </li>
  );
}
