import { useEffect, useMemo, useRef, useState } from "react";
import { useRound, usePlaceBet, useRounds } from "../api/hooks";
import type { BetLine, PlaceBetBody, Round, RoundClosedNotice } from "../api/types";
import { Board } from "../components/Board";
import { Confetti } from "../components/Confetti";
import { Pocket } from "../components/Pocket";
import { useToasts } from "../components/Toasts";
import { WheelCanvas } from "../components/WheelCanvas";
import { StakePicker } from "../components/StakePicker";
import { aggregateChips, totalStake, type ChipDrop, type Spot } from "../lib/board";
import { formatChips } from "../lib/format";
import { errorMessage, failureMessage } from "../lib/messages";
import { pocketColor } from "../lib/roulette";
import { playWinSound } from "../lib/winSound";
import { useWheelClock } from "../lib/useWheelClock";
import { WheelModel } from "../lib/wheel";
import { useRealtime } from "../realtime/RealtimeProvider";

const CHIPS = [1, 10, 50, 100, 500];
/** Cuanto se queda la rueda a la vista, mostrando el numero, despues de que la bola se detiene. */
const RESULT_LINGER_MS = 2_000;

const prefersReducedMotion = () =>
  typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Lo que se muestra al terminar una apuesta, venga del aviso en vivo o de la consulta de respaldo. */
interface Result {
  betId: string;
  status: "Settled" | "Rejected" | "Voided";
  winningNumber: number | null;
  stake: number;
  payout: number;
  failureReason: string | null;
}

/** Ganancia neta: lo cobrado menos lo apostado (200*2 - 250 = 150). Solo si es > 0 se celebra. */
const netWin = (r: Result) => r.payout - r.stake;

const fromNotice =(n: RoundClosedNotice): Result => ({ ...n });
const fromRound = (r: Round): Result => ({
  betId: r.betId,
  status: r.status as Result["status"],
  winningNumber: r.winningNumber,
  stake: r.stake,
  payout: r.status === "Settled" ? (r.payout ?? 0) : 0,
  failureReason: r.failureReason,
});

export function Roulette() {
  const { balance, onRoundClosed, holdBalance } = useRealtime();
  const toasts = useToasts();
  const placeBet = usePlaceBet();
  const recent = useRounds(10);

  const [model] = useState(() => new WheelModel({ reduceMotion: prefersReducedMotion() }));
  const [chip, setChip] = useState(10);
  // Cada toque en el tapete es una ficha; se puede apostar a muchos lugares a la vez (incluso rojo y negro).
  const [drops, setDrops] = useState<ChipDrop[]>([]);
  const [waitingFor, setWaitingFor] = useState<string | null>(null);
  const [spinning, setSpinning] = useState(false);
  const [result, setResult] = useState<Result | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [winPopup, setWinPopup] = useState<Result | null>(null);
  // La rueda vive en un modal que solo se ve mientras gira y un ratito despues, con el numero que salio.
  const [showWheel, setShowWheel] = useState(false);
  const wheelTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Misma apuesta = misma clave de idempotencia: si la respuesta se pierde y el jugador reintenta, el servidor no cobra dos veces.
  const pending = useRef<{ fingerprint: string; key: string } | null>(null);
  // Resultado que el cliente ya conoce pero que se muestra recien cuando la bola se detiene.
  const closedRef = useRef<Result | null>(null);
  const landedRef = useRef(false); // ya se le dijo a la rueda en que numero cae
  const ballDoneRef = useRef(false); // la bola ya se detuvo
  const releaseRef = useRef<(() => void) | null>(null);

  const release = () => {
    releaseRef.current?.();
    releaseRef.current = null;
  };
  useEffect(() => release, []);
  useEffect(() => () => {
    if (wheelTimer.current) clearTimeout(wheelTimer.current);
  }, []);

  const placed = useMemo(() => aggregateChips(drops), [drops]);
  const stake = totalStake(placed);
  const canBet = placed.length > 0 && stake <= balance.available && !spinning && !placeBet.isPending;

  const reveal = (closed: Result) => {
    setResult(closed);
    setSpinning(false);
    setWaitingFor(null);
    release(); // recien ahora el saldo se actualiza
    if (closed.status === "Settled") {
      // La rueda se queda un momento mostrando el numero; recien despues se cierra y aparece el aviso (premio o aviso de que no hubo suerte).
      if (wheelTimer.current) clearTimeout(wheelTimer.current);
      wheelTimer.current = setTimeout(() => {
        setShowWheel(false);
        if (netWin(closed) > 0) {
          setWinPopup(closed);
          playWinSound();
        } else toasts.show("loss", `Salió el ${closed.winningNumber}.`);
      }, RESULT_LINGER_MS);
    } else {
      setShowWheel(false);
      if (closed.status === "Rejected") toasts.show("error", failureMessage(closed.failureReason));
      else toasts.show("info", "La ronda se anuló y tus fichas volvieron a tu saldo.");
    }
  };

  /** El resultado se muestra cuando la bola ya se detuvo Y la ronda ya esta cobrada (lo que llegue ultimo). */
  const tryReveal = () => {
    const closed = closedRef.current;
    if (!closed || !ballDoneRef.current) return;
    closedRef.current = null;
    reveal(closed);
  };

  const phase = useWheelClock(model, () => {
    ballDoneRef.current = true;
    tryReveal();
  });

  /** El servidor ya sorteo el numero (aunque todavia falte cobrar): la bola puede empezar a aterrizar. */
  const onDrawn = (round: Round) => {
    if (round.betId !== waitingFor || landedRef.current || round.winningNumber === null) return;
    landedRef.current = true;
    model.land(round.winningNumber);
  };

  /** La ronda termino. Si hubo numero la bola aterriza en el; si no, no hay nada que animar. */
  const onClosed = (closed: Result) => {
    if (closedRef.current || closed.betId !== waitingFor) return;
    if (closed.status === "Settled" && closed.winningNumber !== null) {
      closedRef.current = closed;
      if (!landedRef.current) {
        landedRef.current = true;
        model.land(closed.winningNumber);
      }
      tryReveal();
    } else {
      model.cancel();
      reveal(closed);
    }
  };
  const onClosedRef = useRef(onClosed);
  const onDrawnRef = useRef(onDrawn);
  useEffect(() => {
    onClosedRef.current = onClosed;
    onDrawnRef.current = onDrawn;
  });

  // Aviso en vivo.
  useEffect(() => onRoundClosed((notice) => onClosedRef.current(fromNotice(notice))), [onRoundClosed]);

  // Respaldo: si el aviso se perdio, la consulta periodica cierra igual la ronda.
  const polled = useRound(waitingFor);
  useEffect(() => {
    const round = polled.data;
    if (!round) return;
    if (["Settled", "Rejected", "Voided"].includes(round.status)) onClosedRef.current(fromRound(round));
    else onDrawnRef.current(round);
  }, [polled.data]);

  const pick = (spot: Spot) => {
    setError(null);
    setDrops((current) => [...current, { spot, amount: chip }]);
  };

  /** Saca la ultima ficha que se puso en ese lugar (un clic derecho en el tapete o el boton de la lista). */
  const unpick = (spot: Spot) => {
    setError(null);
    setDrops((current) => {
      const i = current.map((d) => d.spot.id).lastIndexOf(spot.id);
      return i < 0 ? current : current.filter((_, j) => j !== i);
    });
  };

  const submit = async () => {
    if (placed.length === 0) return;
    setError(null);
    const bets: BetLine[] = placed.map((p) => ({ betType: p.spot.betType, selection: p.spot.selection, stake: p.stake }));
    const body: PlaceBetBody = { bets };
    // La misma tirada (en cualquier orden) = la misma clave de idempotencia.
    const fingerprint = JSON.stringify([...bets].map((b) => `${b.betType}:${b.selection.join("-")}:${b.stake}`).sort());
    if (pending.current?.fingerprint !== fingerprint) pending.current = { fingerprint, key: crypto.randomUUID() };

    // La rueda empieza a girar ya, sin conocer el resultado, y el saldo se congela hasta que la bola caiga.
    setResult(null);
    setSpinning(true);
    setShowWheel(true);
    closedRef.current = null;
    landedRef.current = false;
    ballDoneRef.current = false;
    releaseRef.current = holdBalance();
    model.spin();

    try {
      const accepted = await placeBet.mutateAsync({ body, idempotencyKey: pending.current.key });
      pending.current = null; // aceptada: la proxima apuesta usa otra clave
      setWaitingFor(accepted.betId);
    } catch (e) {
      model.cancel();
      setSpinning(false);
      setShowWheel(false);
      release();
      setError(errorMessage(e));
      // Error definitivo (la API lo rechazo): proxima apuesta con clave nueva. Si fue de red, se conserva para reintentar sin duplicar.
      if (e instanceof Error && "status" in e && (e as { status: number }).status !== 0) pending.current = null;
    }
  };

  const wheelLabel = phase === "settled" && model.result !== null ? `La bola se detuvo en el ${model.result}` : phase === "idle" ? "Ruleta en reposo" : "La ruleta está girando";

  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Ruleta europea</p>
        <h1 className="display">Hacé tu apuesta</h1>
        <p className="muted">El servidor asigna el nonce de cada tirada: nadie puede elegir ni influir el resultado.</p>
      </header>

      <div className="table-grid">
        <section className="card" aria-labelledby="apuesta">
          <h2 id="apuesta" className="section-title">1. Elegí una ficha y tocá el tapete</h2>
          <StakePicker stake={chip} onChange={setChip} chips={CHIPS} min={1} max={Number.MAX_SAFE_INTEGER} allowCustom={false} label="Valor de la ficha" />

          <Board placed={placed} disabled={spinning} winning={result?.status === "Settled" ? result.winningNumber : null} onPick={pick} onRemove={unpick} />

          <div className="summary">
            {placed.length > 0 ? (
              <>
                <ul className="bet-list" aria-label="Tus apuestas">
                  {placed.map((p) => (
                    <li key={p.spot.id}>
                      <span>{p.spot.label}</span>
                      <span className="muted">
                        {formatChips(p.stake)} fichas · si sale, cobrás {formatChips(p.stake * p.spot.multiplier)}
                      </span>
                      <button type="button" className="link-btn" disabled={spinning} aria-label={`Quitar una ficha de ${p.spot.label}`} onClick={() => unpick(p.spot)}>
                        − ficha
                      </button>
                    </li>
                  ))}
                </ul>
                <span>
                  Total apostado: <strong>{formatChips(stake)}</strong> fichas. Podés apostar a varios lugares a la vez; tocá otra vez el mismo lugar para sumar fichas y clic derecho (o “− ficha”) para sacar una.
                </span>
              </>
            ) : (
              <span className="muted">Tocá un número o una apuesta del tapete. Podés apostar a varios lugares a la vez.</span>
            )}
          </div>

          {stake > balance.available && balance.ready && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa apuesta.</p>}
          {error && <p className="notice notice--error" role="alert">{error}</p>}

          <div className="actions dock">
            <span className="actions__group">
              <button type="button" className="btn btn--ghost" disabled={drops.length === 0 || spinning} onClick={() => setDrops((current) => current.slice(0, -1))}>
                Deshacer
              </button>
              <button type="button" className="btn btn--ghost" disabled={drops.length === 0 || spinning} onClick={() => setDrops([])}>
                Quitar todo
              </button>
            </span>
            <button type="button" className="btn btn--gold btn--lg" disabled={!canBet} onClick={() => void submit()}>
              {spinning ? "Girando…" : placeBet.isPending ? "Enviando…" : placed.length > 0 ? `Apostar ${formatChips(stake)} fichas` : "Apostar"}
            </button>
          </div>
        </section>

        <section className="card result" aria-labelledby="resultado" aria-live="polite">
          <h2 id="resultado" className="section-title">La ruleta</h2>
          {!spinning && !result && <p className="muted">Tu próxima tirada aparece acá.</p>}
          {spinning && <p role="status">La ruleta está girando…</p>}
          {!spinning && result && <ResultView result={result} />}

          <h3 className="section-subtitle">Últimos números</h3>
          <div className="strip">
            {/* La tirada en curso no se muestra hasta que la bola se detiene (si no, adelanta el resultado). */}
            {recent.data?.filter((r) => r.winningNumber !== null && r.status === "Settled" && !(spinning && r.betId === waitingFor)).slice(0, 10).map((r) => (
              <Pocket key={r.betId} number={r.winningNumber!} size="sm" />
            ))}
            {recent.data?.every((r) => r.winningNumber === null) && <span className="muted">Sin tiradas todavía.</span>}
          </div>
        </section>
      </div>

      <div className={`wheel-modal ${showWheel ? "wheel-modal--open" : ""}`} aria-hidden={!showWheel} data-testid="wheel-modal">
        <div className="wheel-modal__box">
          <WheelCanvas model={model} phase={phase} label={wheelLabel} />
          <div className="wheel-modal__result" aria-hidden="true">
            {phase === "settled" && model.result !== null ? <Pocket number={model.result} size="xl" /> : null}
          </div>
        </div>
      </div>

      {winPopup && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWinPopup(null)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <Pocket number={winPopup.winningNumber ?? 0} size="xl" />
            <h2 id="win-title" className="win-modal__title">¡Ganaste!</h2>
            <p className="win-modal__amount">{formatChips(netWin(winPopup))} fichas</p>
            <p className="muted">Salió el {winPopup.winningNumber}. Apostaste {formatChips(winPopup.stake)} y cobraste {formatChips(winPopup.payout)}.</p>
            <button type="button" className="btn btn--gold" autoFocus onClick={() => setWinPopup(null)}>
              Continuar
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function ResultView({ result }: { result: Result }) {
  if (result.status === "Rejected") {
    return (
      <div className="outcome outcome--bad">
        <p className="outcome__title">Apuesta rechazada</p>
        <p>{failureMessage(result.failureReason)}</p>
      </div>
    );
  }
  if (result.status === "Voided") {
    return (
      <div className="outcome">
        <p className="outcome__title">Ronda anulada</p>
        <p>Tus fichas volvieron a tu saldo.</p>
      </div>
    );
  }

  const net = netWin(result);
  const won = net > 0;
  const color = pocketColor(result.winningNumber ?? 0);
  return (
    <div className={`outcome ${won ? "outcome--win" : "outcome--loss"}`}>
      <Pocket number={result.winningNumber ?? 0} size="xl" />
      <p className="outcome__title">{won ? `Ganaste ${formatChips(net)} fichas` : "No hubo suerte esta vez"}</p>
      <p className="muted">Salió el {result.winningNumber} ({color === "red" ? "rojo" : color === "black" ? "negro" : "verde"}). Apostaste {formatChips(result.stake)} y cobraste {formatChips(result.payout)}.</p>
    </div>
  );
}
