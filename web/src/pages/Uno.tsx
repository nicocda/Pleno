import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type CSSProperties } from "react";
import { queryKeys, useTableAction } from "../api/hooks";
import type { TableSeat, UnoAction, UnoView } from "../api/types";
import { Confetti } from "../components/Confetti";
import { TablesLobby, type LiveTable } from "../components/TablesLobby";
import { sha256Hex } from "../lib/crash";
import { formatChips } from "../lib/format";
import { detailedErrorMessage } from "../lib/messages";
import { COLORS, COLOR_CSS, colorName, decodeCard, eventText, isWild, playBody, textOn } from "../lib/uno";
import { playWinSound } from "../lib/winSound";

const GAME_ID = "uno";

/** Uno: mesas entre jugadores (y bots). Quien se queda sin cartas se lleva todo lo que se puso en la mesa. */
export function Uno() {
  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Uno</p>
        <h1 className="display">Quedate sin cartas</h1>
        <p className="muted">Armá una mesa con otros jugadores (o con bots), poné la entrada y el primero que se queda sin cartas se lleva todo. El mazo se baraja con una semilla comprometida de antemano y se revela al terminar.</p>
      </header>
      <TablesLobby<UnoView> gameId={GAME_ID} gameName="Uno" renderGame={(table, leave) => <UnoTable table={table} onLeave={leave} />} />
      <Rules />
    </div>
  );
}

function Rules() {
  return (
    <section className="card" aria-labelledby="uno-reglas">
      <h2 id="uno-reglas" className="section-title">Reglas</h2>
      <ul className="uno-rules">
        <li>Jugás una carta del mismo color, del mismo número o símbolo que la de arriba, o un comodín. Si no tenés, robás una: si se puede jugar, la jugás o pasás.</li>
        <li>Salto (⊘) saltea al que sigue; reversa (⇄) cambia el sentido (con 2 jugadores funciona como salto); +2 y +4 hacen robar al que sigue.</li>
        <li>Con un comodín elegís el color que sigue.</li>
        <li>El "Uno" es automático: cuando alguien queda con una carta, se avisa en la mesa.</li>
        <li>Cada turno dura 30 segundos. Si no jugás 3 turnos seguidos quedás ausente y un bot juega por vos. No se puede salir en plena partida.</li>
        <li>Quien se queda sin cartas gana la suma de todas las entradas.</li>
      </ul>
    </section>
  );
}

/** Una carta con CSS: sin imagenes. Con `onClick` es un boton; sin el, una imagen para lectores de pantalla. */
function UnoCard({ card, label, onClick, disabled, playable, dimmed, drawnIndex, size = "md" }: { card: number; label?: string; onClick?: () => void; disabled?: boolean; playable?: boolean; dimmed?: boolean; drawnIndex?: number; size?: "md" | "lg" }) {
  const info = decodeCard(card);
  const style = { "--uno-bg": info.color === null ? undefined : COLOR_CSS[info.color], "--uno-fg": textOn(info.color), "--draw-delay": drawnIndex === undefined ? undefined : `${drawnIndex * 120}ms` } as CSSProperties;
  const classes = ["uno-card", `uno-card--${size}`, info.color === null ? "uno-card--wild" : "", playable ? "uno-card--playable" : "", dimmed ? "uno-card--dim" : "", drawnIndex !== undefined ? "uno-card--drawn" : ""].filter(Boolean).join(" ");
  const face = (
    <>
      <span className="uno-card__corner" aria-hidden="true">{info.symbol}</span>
      <span className="uno-card__symbol" aria-hidden="true">{info.symbol}</span>
    </>
  );
  if (onClick) {
    return (
      <button type="button" className={classes} style={style} aria-label={label ?? info.label} disabled={disabled} onClick={onClick}>
        {face}
      </button>
    );
  }
  return (
    <span className={classes} style={style} role="img" aria-label={label ?? info.label}>
      {face}
    </span>
  );
}

const secondsUntil = (iso: string | null, serverNowMs: number): number => (iso === null ? 0 : Math.max(0, Math.ceil((Date.parse(iso) - serverNowMs) / 1000)));

type Verification = { ok: boolean } | null;

function UnoTable({ table, onLeave }: { table: LiveTable<UnoView>; onLeave: () => void }) {
  const queryClient = useQueryClient();
  const act = useTableAction<UnoAction>(GAME_ID, table.id);
  const game = table.game;
  const [error, setError] = useState<string | null>(null);
  const [picking, setPicking] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [sawPlaying, setSawPlaying] = useState(false);
  const [winClosed, setWinClosed] = useState(false);
  const [verification, setVerification] = useState<Verification>(null);
  const [handKey, setHandKey] = useState<string | null>(null);
  const [prevHand, setPrevHand] = useState<number[] | null>(null);
  const [freshCards, setFreshCards] = useState<number[]>([]);

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(id);
  }, []);

  const finished = table.status === "Finished";
  const mySeat = table.mySeat;
  const winner = game?.winner ?? -1;
  const payout = mySeat !== null ? table.payouts?.[mySeat] ?? null : null;
  const net = payout === null ? null : payout - table.buyIn;
  const iWon = finished && mySeat !== null && winner === mySeat;

  // Se festeja solo una victoria que se vio resolverse en esta pantalla (no una vieja que quedo del estado al entrar).
  if (table.status === "Playing" && !sawPlaying) setSawPlaying(true);
  const celebrate = finished && sawPlaying && iWon && net !== null && !winClosed;
  const justFinished = finished && sawPlaying;
  useEffect(() => {
    if (justFinished) void queryClient.invalidateQueries({ queryKey: queryKeys.account });
  }, [justFinished, queryClient]);
  useEffect(() => {
    if (celebrate) playWinSound();
  }, [celebrate]);

  // Las cartas que aparecen en la mano despues de la primera vez son robadas: se animan saliendo del mazo (la primera carga no anima).
  const currentHand = game?.hand;
  if (currentHand && currentHand.join(",") !== handKey) {
    setHandKey(currentHand.join(","));
    setPrevHand(currentHand);
    setFreshCards(prevHand === null ? [] : currentHand.filter((c) => !prevHand.includes(c)));
  }

  const nameOf = (seat: number): string => table.seats.find((s) => s.seat === seat)?.name ?? `Asiento ${seat + 1}`;
  const serverNow = now + table.offsetMs;

  if (!game) return <p className="muted" role="status">Repartiendo las cartas…</p>;

  const myTurn = table.status === "Playing" && mySeat !== null && game.current === mySeat;
  const turnSeconds = secondsUntil(table.turnEndsAt, serverNow);
  const hand = [...game.hand].sort((a, b) => a - b);
  const mustPlayOrPass = myTurn && game.drawnCard !== null;
  const mustDraw = myTurn && game.playable.length === 0 && game.drawnCard === null;
  const pileLevel = game.drawCount <= 5 ? "low" : game.drawCount <= 20 ? "mid" : "full";
  const others = game.players.filter((p) => p.seat !== mySeat).sort((a, b) => a.seat - b.seat);
  const unoSeats = table.status === "Playing" ? game.players.filter((p) => p.cards === 1).map((p) => p.seat) : [];

  const send = (action: UnoAction) => {
    setError(null);
    setPicking(null);
    act.mutate(action, { onError: (e) => setError(detailedErrorMessage(e)) });
  };

  const playCard = (card: number) => {
    if (isWild(card)) setPicking(card);
    else send(playBody(card));
  };

  const statusLabel = finished ? (winner < 0 ? "La partida terminó sin ganador" : winner === mySeat ? "¡Ganaste!" : `Ganó ${nameOf(winner)}`)
    : mySeat === null ? `Mirando la partida: juega ${nameOf(game.current)}`
    : myTurn ? (game.drawnCard !== null ? `Robaste ${decodeCard(game.drawnCard).label}: jugala o pasá (${turnSeconds} s)` : `Es tu turno: jugá una carta o robá (${turnSeconds} s)`)
    : `Juega ${nameOf(game.current)}${table.seats.find((s) => s.seat === game.current)?.away ? " (ausente: lo juega un bot)" : ""}`;

  const proof = async () => {
    if (table.serverSeed === null) return;
    setVerification({ ok: (await sha256Hex(table.serverSeed)) === table.commitment.toLowerCase() });
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="uno-mesa">
        <div className="uno-head">
          <h2 id="uno-mesa" className="section-title">{table.name}</h2>
          <p className="muted">Entrada de {formatChips(table.buyIn)} fichas · en juego {formatChips(table.buyIn * table.seats.length)}</p>
        </div>
        <p role="status" className="crash-status" data-testid="uno-status">{statusLabel}</p>
        {mustDraw && (
          <p role="status" className="uno-alert uno-alert--draw" data-testid="uno-draw-alert">
            No tenés nada para jugar: tocá el mazo para robar.
          </p>
        )}
        {unoSeats.length > 0 && (
          <p role="status" className="uno-alert" data-testid="uno-alert">
            {unoSeats.map((seat) => `${seat === mySeat ? "Quedaste con una carta" : `${nameOf(seat)} quedó con una carta`}`).join(" · ")}: ¡Uno!
          </p>
        )}

        <div className="uno-felt">
          <ul className="uno-others" aria-label="Los otros jugadores">
            {others.map((p) => (
              <OpponentView key={p.seat} seat={table.seats.find((s) => s.seat === p.seat)} cards={p.cards} active={table.status === "Playing" && game.current === p.seat} turnSeconds={turnSeconds} winner={finished && winner === p.seat} />
            ))}
            {others.length === 0 && <li className="muted">No hay más jugadores.</li>}
          </ul>

          <div className="uno-center">
            <button type="button" className={`uno-pile ${mustDraw ? "uno-pile--call" : ""}`} data-level={pileLevel} aria-label="Robar carta" disabled={!myTurn || game.drawnCard !== null || act.isPending} onClick={() => send({ type: "draw" })}>
              <span className="uno-pile__back" aria-hidden="true">UNO</span>
              <span className="uno-pile__count">{game.drawCount} en el mazo</span>
              {mustDraw && <span className="uno-pile__hint" aria-hidden="true">¡Tocá para robar!</span>}
            </button>
            <div className="uno-top">
              <span className="uno-discard">
                <UnoCard card={game.top} size="lg" label={`Carta de arriba: ${decodeCard(game.top).label}`} />
              </span>
              <p className="uno-color" data-testid="uno-color">
                <span className="uno-color__dot" style={{ background: COLOR_CSS[game.topColor] }} aria-hidden="true" />
                Color en juego: <strong>{colorName(game.topColor)}</strong>
              </p>
              <p className="muted uno-direction" data-testid="uno-direction">
                Sentido: <span aria-hidden="true">{game.direction === 1 ? "↻" : "↺"}</span> {game.direction === 1 ? "horario" : "antihorario"}
              </p>
            </div>
          </div>

          {mySeat !== null && (
            <div className="uno-mine dock">
              <p className="bj-who">
                Tu mano ({hand.length} {hand.length === 1 ? "carta" : "cartas"})
                {myTurn ? ` · tu turno (${turnSeconds} s)` : ""}
              </p>
              <div className="uno-hand" data-testid="uno-hand" role="group" aria-label="Tu mano">
                {hand.map((card) => {
                  const fresh = freshCards.includes(card);
                  const playable = myTurn && game.playable.includes(card);
                  return (
                    <UnoCard
                      key={card}
                      card={card}
                      drawnIndex={fresh ? freshCards.indexOf(card) : undefined}
                      playable={playable}
                      dimmed={!playable && table.status === "Playing"}
                      disabled={!playable || act.isPending}
                      label={playable ? `Jugar ${decodeCard(card).label}` : decodeCard(card).label}
                      onClick={() => playCard(card)}
                    />
                  );
                })}
                {hand.length === 0 && <span className="muted">Sin cartas</span>}
              </div>

              {picking !== null && (
                <div className="uno-picker" role="group" aria-label="Elegí un color">
                  <p>Elegí el color con el que seguís:</p>
                  <div className="uno-picker__colors">
                    {COLORS.map((color) => (
                      <button key={color} type="button" className="uno-picker__color" style={{ background: COLOR_CSS[color], color: textOn(color) }} aria-label={`Color ${colorName(color)}`} disabled={act.isPending} onClick={() => send(playBody(picking, color))}>
                        {colorName(color)}
                      </button>
                    ))}
                  </div>
                  <button type="button" className="link-btn" onClick={() => setPicking(null)}>Cancelar</button>
                </div>
              )}

              {mustPlayOrPass && (
                <button type="button" className="btn btn--ghost btn--lg" disabled={act.isPending} onClick={() => send({ type: "pass" })}>Pasar</button>
              )}
            </div>
          )}
          {error && <p className="notice notice--error" role="alert">{error}</p>}
        </div>

        {finished && (
          <div role="status" className="uno-result" data-testid="uno-result">
            <p><strong>{winner < 0 ? "Nadie ganó: se devolvieron las entradas." : iWon ? `¡Ganaste! ${net !== null && net >= 0 ? "+" : ""}${formatChips(net ?? 0)} fichas` : `Ganó ${nameOf(winner)}.`}</strong></p>
            {payout !== null && (
              <p className="muted">
                Pusiste {formatChips(table.buyIn)} y cobraste {formatChips(payout)}: {net! >= 0 ? "ganancia" : "pérdida"} neta de {formatChips(Math.abs(net!))} fichas.
              </p>
            )}
            <button type="button" className="btn btn--gold" onClick={onLeave}>Volver a las mesas</button>
          </div>
        )}
      </section>

      <section className="card" aria-labelledby="uno-hist">
        <h2 id="uno-hist" className="section-title">Lo último que pasó</h2>
        <ol className="uno-events" role="log" aria-label="Historial de la partida">
          {[...game.events].reverse().map((event, i) => (
            <li key={`${game.events.length - i}-${event.kind}-${event.seat}`}>{eventText(event, nameOf)}</li>
          ))}
          {game.events.length === 0 && <li className="muted">Todavía no jugó nadie.</li>}
        </ol>
      </section>

      <section className="card" aria-labelledby="uno-verificar">
        <h2 id="uno-verificar" className="section-title">Verificá la partida</h2>
        <p className="muted">Antes de repartir el servidor publicó el compromiso (el hash de su semilla). Al terminar la partida revela la semilla: si su hash coincide con el compromiso, el mazo no se cambió durante el juego.</p>
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
          <p className="muted">La semilla se revela cuando termina la partida.</p>
        )}
      </section>

      {celebrate && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWinClosed(true)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <p className="crash-multiplier crash-multiplier--running" aria-hidden="true">UNO</p>
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

function OpponentView({ seat, cards, active, turnSeconds, winner }: { seat: TableSeat | undefined; cards: number; active: boolean; turnSeconds: number; winner: boolean }) {
  const name = seat?.name ?? "Jugador";
  const [prev, setPrev] = useState(cards);
  const [gained, setGained] = useState(0);
  if (cards !== prev) {
    setPrev(cards);
    setGained(cards > prev ? Math.min(cards - prev, 4) : 0);
  }
  const classes = ["uno-opp", active ? "uno-opp--active" : "", winner ? "uno-opp--winner" : ""].filter(Boolean).join(" ");
  return (
    <li className={classes} aria-current={active ? "true" : undefined} data-testid="uno-opp">
      <p className="bj-who">
        {name}
        {active ? ` · turno (${turnSeconds} s)` : ""}
      </p>
      <p className="uno-opp__cards" aria-label={`${cards} ${cards === 1 ? "carta" : "cartas"}`}>
        <span className="uno-opp__back" aria-hidden="true">{cards}</span>
        {Array.from({ length: gained }, (_, i) => (
          <span key={`${cards}-${i}`} className="uno-opp__flying" style={{ "--draw-delay": `${i * 120}ms` } as CSSProperties} aria-hidden="true" />
        ))}
        <span className="muted">{cards === 1 ? "carta" : "cartas"}</span>
      </p>
      {seat?.isBot && <p className="uno-opp__tag">Bot</p>}
      {seat?.away && !seat.isBot && <p className="uno-opp__tag">Ausente: lo juega un bot</p>}
      {cards === 1 && <p className="uno-opp__tag uno-opp__tag--uno">¡Uno!</p>}
    </li>
  );
}
