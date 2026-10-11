import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type CSSProperties } from "react";
import { queryKeys, useTableAction } from "../api/hooks";
import type { TrucoAction, TrucoActionType, TrucoEvent, TrucoPlay, TrucoView } from "../api/types";
import { Confetti } from "../components/Confetti";
import { SpanishCardFace } from "../components/SpanishCard";
import { TablesLobby, type LiveTable } from "../components/TablesLobby";
import { sha256Hex } from "../lib/crash";
import { formatChips } from "../lib/format";
import { detailedErrorMessage } from "../lib/messages";
import { MANO_NAMES, SUIT_CSS, actionLabel, bannerFor, byStrength, decodeCard, eventText, groupOf, GROUP_LABELS, newEvents, pendingText, pileSpot, playBody, trucoLevelName, trucoWorth, type ActionGroup, type Banner } from "../lib/truco";
import { playWinSound } from "../lib/winSound";

const GAME_ID = "truco";

/** Truco: mesas de 2 jugadores (o jugador contra bot). El primero que llega a los puntos de la partida se lleva todo lo que se puso en la mesa. */
export function Truco() {
  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Truco</p>
        <h1 className="display">Truco, quiero retruco</h1>
        <p className="muted">Jugá al truco argentino contra otra persona o contra un bot, con la entrada que quieras. Quien llega primero a 15 puntos se lleva todo. El mazo se baraja con una semilla comprometida de antemano y se revela al terminar.</p>
      </header>
      <TablesLobby<TrucoView> gameId={GAME_ID} gameName="Truco" renderGame={(table, leave) => <TrucoTable table={table} onLeave={leave} />} />
      <Rules />
    </div>
  );
}

function Rules() {
  return (
    <section className="card" aria-labelledby="tru-reglas">
      <h2 id="tru-reglas" className="section-title">Reglas</h2>
      <ul className="tru-rules">
        <li>Baraja española de 40 cartas, sin flor. Cada ronda se reparten 3 cartas y se juegan hasta 3 manos: gana la ronda quien gana 2 (si hay parda desempata quien es mano, el que empieza la ronda).</li>
        <li>De mayor a menor: 1 de espadas, 1 de bastos, 7 de espadas, 7 de oros, los 3, los 2, los 1 de copas y oros, 12, 11, 10, los 7 de copas y bastos, 6, 5 y 4.</li>
        <li>Envido, en la primera mano: se cuentan 20 más las dos mejores cartas del mismo palo (las figuras valen 0); sin dos del mismo palo, vale la carta más alta. Se puede subir con real envido y falta envido.</li>
        <li>Truco: vale 2 puntos la ronda; se sube con retruco (3) y vale cuatro (4). Si no querés, el otro suma los puntos que valía antes del canto.</li>
        <li>"Al mazo" abandona la ronda y el otro se lleva los puntos que estén en juego.</li>
        <li>Gana quien llega a 15 puntos y se lleva la suma de las entradas.</li>
        <li>Cada turno dura 45 segundos. Si no jugás 3 turnos seguidos quedás ausente y un bot juega por vos.</li>
      </ul>
    </section>
  );
}

/** Una carta con CSS: sin imagenes. Con `onClick` es un boton; sin el, una imagen para lectores de pantalla. */
function TrucoCard({ card, label, onClick, disabled, playable, dimmed }: { card: number; label?: string; onClick?: () => void; disabled?: boolean; playable?: boolean; dimmed?: boolean }) {
  const info = decodeCard(card);
  const style = { "--tru-suit": SUIT_CSS[info.suit] } as CSSProperties;
  const classes = ["tru-card", playable ? "tru-card--playable" : "", dimmed ? "tru-card--dim" : ""].filter(Boolean).join(" ");
  const face = <SpanishCardFace suit={info.suit} number={info.number} />;
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
/** La ronda que acaba de terminar: sus cartas y el resultado de cada mano. */
type Recap = { handNo: number; table: TrucoPlay[]; manos: number[] };
const RECAP_MS = 3_500;
type CallType = Exclude<TrucoActionType, "play">;
const GROUPS: ActionGroup[] = ["envido", "truco", "response"];
/** El servidor ordena las jugadas alfabeticamente: en pantalla van de menor a mayor canto y "Quiero" antes que "No quiero". */
const ORDER: readonly CallType[] = ["envido", "real_envido", "falta_envido", "truco", "retruco", "vale4", "quiero", "no_quiero", "mazo"];

function TrucoTable({ table, onLeave }: { table: LiveTable<TrucoView>; onLeave: () => void }) {
  const queryClient = useQueryClient();
  const act = useTableAction<TrucoAction>(GAME_ID, table.id);
  const game = table.game;
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [sawPlaying, setSawPlaying] = useState(false);
  const [winClosed, setWinClosed] = useState(false);
  const [verification, setVerification] = useState<Verification>(null);

  // Al terminar una ronda el servidor reparte la siguiente de inmediato: se guardan las cartas de la que acaba de terminar para dejarlas a la vista unos segundos.
  const [seen, setSeen] = useState<TrucoView | null>(null);
  const [recap, setRecap] = useState<Recap | null>(null);
  if (game && (seen === null || seen.handNo !== game.handNo || seen.table.length !== game.table.length)) {
    if (seen !== null && game.handNo > seen.handNo && seen.table.length > 0) setRecap({ handNo: seen.handNo, table: seen.table, manos: seen.bazas });
    setSeen(game);
  }

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(id);
  }, []);

  // Carteles: cada hecho nuevo de la partida (cantos, respuestas, manos, envido, ronda) se avisa en grande unos segundos. Lo que ya habia al entrar no se anuncia.
  const [eventsSeen, setEventsSeen] = useState<TrucoEvent[] | null>(null);
  const [banners, setBanners] = useState<(Banner & { id: number; until: number })[]>([]);
  const eventsKey = game ? JSON.stringify(game.events) : null;
  const [seenKey, setSeenKey] = useState<string | null>(null);
  if (game && eventsKey !== seenKey) {
    setSeenKey(eventsKey);
    if (eventsSeen !== null) {
      const fresh = newEvents(eventsSeen, game.events).slice(-4);
      const nameFor = (seat: number): string => table.seats.find((s) => s.seat === seat)?.name ?? `Asiento ${seat + 1}`;
      const made = fresh.flatMap((event, i) => {
        const banner = bannerFor(event, table.mySeat, nameFor);
        return banner === null ? [] : [{ ...banner, id: now + i, until: now + banner.ms }];
      });
      if (made.length > 0) setBanners((current) => [...current.filter((b) => b.until > now), ...made].slice(-3));
    }
    setEventsSeen(game.events);
  }
  useEffect(() => {
    if (!recap) return;
    const id = window.setTimeout(() => setRecap(null), RECAP_MS);
    return () => window.clearTimeout(id);
  }, [recap]);

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

  const nameOf = (seat: number): string => table.seats.find((s) => s.seat === seat)?.name ?? `Asiento ${seat + 1}`;
  const serverNow = now + table.offsetMs;

  if (!game) return <p className="muted" role="status">Repartiendo las cartas…</p>;

  const me = mySeat ?? 0;
  const rival = 1 - me;
  const playing = table.status === "Playing";
  const myTurn = playing && mySeat !== null && game.current === mySeat;
  const turnSeconds = secondsUntil(table.turnEndsAt, serverNow);
  const canPlayCard = myTurn && game.actions.includes("play");
  const hand = [...game.hand].sort(byStrength);
  const callActions = game.actions.filter((a): a is CallType => a !== "play").sort((a, b) => ORDER.indexOf(a) - ORDER.indexOf(b));
  const rivalSeat = table.seats.find((s) => s.seat === rival);

  const send = (action: TrucoAction) => {
    setError(null);
    act.mutate(action, { onError: (e) => setError(detailedErrorMessage(e)) });
  };

  // Las cartas de la mesa van en UNA pila (la segunda mano se juega encima de la primera). Si acaba de terminar una ronda, se ve esa ronda unos segundos.
  const showingRecap = recap !== null && game.table.length === 0;
  const pile = showingRecap ? recap.table : game.table;
  const manos = showingRecap ? recap.manos : game.bazas;
  const manoCount = Math.max(Math.ceil(pile.length / 2), manos.length);
  const manoResult = (result: number | undefined): string =>
    result === undefined ? "en juego" : result === -1 ? "parda" : mySeat === null ? `ganó ${nameOf(result)}` : result === mySeat ? "ganaste" : "perdiste";
  const lastEnd = [...game.events].reverse().find((e) => e.kind === "hand_end");
  const recapText = showingRecap && lastEnd
    ? `Ronda ${recap.handNo}: ${mySeat !== null && lastEnd.seat === mySeat ? "ganaste" : `ganó ${nameOf(lastEnd.seat)}`}${lastEnd.value === null ? "" : ` +${lastEnd.value}`}`
    : null;

  const pendingByRival = game.pending !== null && game.pending.caller !== mySeat;
  const worth = trucoWorth(game.trucoLevel);

  const statusLabel = finished ? (winner < 0 ? "La partida terminó sin ganador" : winner === mySeat ? "¡Ganaste!" : `Ganó ${nameOf(winner)}`)
    : mySeat === null ? `Mirando la partida: juega ${nameOf(game.current)}`
    : myTurn ? (game.pending !== null ? `Te cantaron: respondé (${turnSeconds} s)` : `Es tu turno: jugá una carta o cantá (${turnSeconds} s)`)
    : table.seats.find((s) => s.seat === game.current)?.isBot ? `${nameOf(game.current)} está pensando…`
    : `Juega ${nameOf(game.current)}${table.seats.find((s) => s.seat === game.current)?.away ? " (ausente: lo juega un bot)" : ""}`;

  const proof = async () => {
    if (table.serverSeed === null) return;
    setVerification({ ok: (await sha256Hex(table.serverSeed)) === table.commitment.toLowerCase() });
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="tru-mesa">
        <div className="tru-head">
          <h2 id="tru-mesa" className="section-title">{table.name}</h2>
          <p className="muted">Entrada de {formatChips(table.buyIn)} fichas · en juego {formatChips(table.buyIn * table.seats.length)}</p>
        </div>
        <p role="status" className="crash-status" data-testid="tru-status">{statusLabel}</p>

        <div className="tru-felt">
          <div className="tru-banners" role="status" aria-live="polite" data-testid="tru-banners">
            {banners.filter((b) => b.until > now).map((b) => (
              <p key={b.id} className={`tru-banner tru-banner--${b.tone}`} data-testid="tru-banner">
                <strong>{b.title}</strong>
                {b.detail && <span>{b.detail}</span>}
              </p>
            ))}
          </div>
          <div className="tru-score" data-testid="tru-score" role="group" aria-label={`Marcador, a ${game.target} puntos`}>
            <p className={`tru-score__side ${playing && game.current === me ? "tru-score__side--active" : ""}`}>
              <span className="tru-score__name">{mySeat === null ? nameOf(me) : "Vos"}{game.mano === me ? " · es mano" : ""}</span>
              <strong className="tru-score__points" data-testid="tru-score-me">{game.scores[me] ?? 0}</strong>
            </p>
            <p className="muted tru-score__target">a {game.target}</p>
            <p className={`tru-score__side ${playing && game.current === rival ? "tru-score__side--active" : ""}`}>
              <span className="tru-score__name">{nameOf(rival)}{game.mano === rival ? " · es mano" : ""}</span>
              <strong className="tru-score__points" data-testid="tru-score-rival">{game.scores[rival] ?? 0}</strong>
            </p>
          </div>
          <p className="tru-info" data-testid="tru-info">
            Ronda {game.handNo} · {game.trucoLevel > 0 ? `${trucoLevelName(game.trucoLevel)} aceptado` : "sin truco"} · vale {worth} {worth === 1 ? "punto" : "puntos"}
          </p>

          <div className="tru-board">
          <div className="tru-opp" data-testid="tru-opp" aria-current={playing && game.current === rival ? "true" : undefined}>
            <p className="bj-who">
              {nameOf(rival)}
              {playing && game.current === rival ? ` · turno (${turnSeconds} s)` : ""}
              {rivalSeat?.isBot ? " · Bot" : ""}
              {rivalSeat?.away && !rivalSeat.isBot ? " · ausente: lo juega un bot" : ""}
            </p>
            <div className="tru-fan" style={{ "--n": game.opponentCards } as CSSProperties} role="img" aria-label={`${nameOf(rival)} tiene ${game.opponentCards} ${game.opponentCards === 1 ? "carta" : "cartas"}`}>
              {Array.from({ length: game.opponentCards }, (_, i) => (
                <span key={i} className="tru-card tru-card--back tru-fan__card" style={{ "--i": i } as CSSProperties} aria-hidden="true" />
              ))}
              <span className="tru-fan__hand" aria-hidden="true" />
              {game.opponentCards === 0 && <span className="tru-none tru-fan__none">Sin cartas</span>}
            </div>
          </div>

          <div className="tru-table" data-testid="tru-table" role="group" aria-label="Cartas jugadas en esta ronda">
            {recapText && <p className="tru-recap" role="status" data-testid="tru-recap">{recapText}</p>}
            <div className="tru-pile" data-testid="tru-pile">
              {pile.map((play, i) => {
                const spot = pileSpot(i);
                const style = { "--dx": `${spot.dx}px`, "--dy": `${spot.dy}px`, "--rot": `${spot.rot}deg`, zIndex: i + 1 } as CSSProperties;
                return (
                  <div key={`${showingRecap ? "r" : "p"}${i}`} className="tru-pile__card" data-mano={Math.floor(i / 2) + 1} style={style}>
                    <TrucoCard card={play.card} label={`${play.seat === mySeat ? "Jugaste" : `${nameOf(play.seat)} jugó`} ${decodeCard(play.card).label}`} />
                  </div>
                );
              })}
              {pile.length === 0 && <p className="tru-none">Todavía no se jugó ninguna carta.</p>}
            </div>
            <ol className="tru-manos" aria-label="Resultado de cada mano" data-testid="tru-manos">
              {Array.from({ length: manoCount }, (_, i) => (
                <li key={i} data-testid="tru-mano" className={manos[i] === undefined ? "" : manos[i] === -1 ? "tru-mano--tie" : manos[i] === mySeat ? "tru-mano--won" : "tru-mano--lost"}>
                  {MANO_NAMES[i] ?? `Mano ${i + 1}`}: {manoResult(manos[i])}
                </li>
              ))}
            </ol>
          </div>
          </div>

          {playing && game.pending !== null && (
            <p role="status" className="tru-pending" data-testid="tru-pending">
              {pendingText(game.pending, nameOf)}
              {pendingByRival ? "" : " Esperando la respuesta."}
            </p>
          )}

          {mySeat !== null && (
            <div className="tru-mine dock">
              <p className="bj-who">
                Tus cartas ({hand.length})
                {myTurn ? ` · tu turno (${turnSeconds} s)` : ""}
              </p>
              <div className="tru-hand" data-testid="tru-hand" role="group" aria-label="Tus cartas">
                {hand.map((card) => (
                  <TrucoCard
                    key={card}
                    card={card}
                    playable={canPlayCard}
                    dimmed={!canPlayCard && playing}
                    disabled={!canPlayCard || act.isPending}
                    label={canPlayCard ? `Jugar ${decodeCard(card).label}` : decodeCard(card).label}
                    onClick={() => send(playBody(card))}
                  />
                ))}
                {hand.length === 0 && <span className="tru-none">Sin cartas</span>}
              </div>
              {game.envidoPoints !== null && playing && (
                <p className="tru-envido" data-testid="tru-envido">Tus puntos de envido: <strong>{game.envidoPoints}</strong> <span className="tru-none">(solo los ves vos)</span></p>
              )}

              {playing && myTurn && callActions.length > 0 && (
                <div className="tru-calls" role="group" aria-label="Cantos">
                  {GROUPS.map((group) => {
                    const items = callActions.filter((a) => groupOf(a) === group);
                    if (items.length === 0) return null;
                    return (
                      <div key={group} className="tru-calls__group" role="group" aria-label={GROUP_LABELS[group]}>
                        <span className="tru-calls__title">{GROUP_LABELS[group]}</span>
                        {items.map((type) => (
                          <button key={type} type="button" className={`btn ${type === "no_quiero" ? "btn--ghost" : "btn--gold"}`} disabled={act.isPending} onClick={() => send({ type })}>
                            {actionLabel(type, game.pending)}
                          </button>
                        ))}
                      </div>
                    );
                  })}
                  {callActions.includes("mazo") && (
                    <div className="tru-calls__group tru-calls__group--mazo" role="group" aria-label={GROUP_LABELS.mazo}>
                      <button type="button" className="btn btn--ghost" disabled={act.isPending} onClick={() => send({ type: "mazo" })}>{actionLabel("mazo", game.pending)}</button>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
          {error && <p className="notice notice--error" role="alert">{error}</p>}
        </div>

        {finished && (
          <div role="status" className="tru-result" data-testid="tru-result">
            {mySeat !== null && winner >= 0 && <p className={`tru-result__headline ${iWon ? "tru-result__headline--won" : "tru-result__headline--lost"}`}>{iWon ? "¡Ganaste la partida!" : "Perdiste la partida"}</p>}
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

      <section className="card" aria-labelledby="tru-hist">
        <h2 id="tru-hist" className="section-title">Lo último que pasó</h2>
        <ol className="tru-events" role="log" aria-label="Historial de la partida">
          {[...game.events].reverse().slice(0, 12).map((event, i) => (
            <li key={`${game.events.length - i}-${event.kind}-${event.seat}`}>{eventText(event, nameOf)}</li>
          ))}
          {game.events.length === 0 && <li className="muted">Todavía no jugó nadie.</li>}
        </ol>
      </section>

      <section className="card" aria-labelledby="tru-verificar">
        <h2 id="tru-verificar" className="section-title">Verificá la partida</h2>
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
            <p className="crash-multiplier crash-multiplier--running" aria-hidden="true">TRUCO</p>
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
