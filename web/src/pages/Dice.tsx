import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useDiceRound, useDiceRounds, usePlaceDice } from "../api/hooks";
import type { DiceDirection, DiceRound, RoundClosedNotice } from "../api/types";
import { Confetti } from "../components/Confetti";
import { StakePicker } from "../components/StakePicker";
import { useToasts } from "../components/Toasts";
import { DICE_ROLLS, MAX_CHANCE, MAX_STAKE, MIN_CHANCE, MIN_STAKE, formatHundredths, limitLabel, multiplierLabel, payoutFor, wins } from "../lib/dice";
import { formatChips } from "../lib/format";
import { errorMessage, failureMessage } from "../lib/messages";
import { playWinSound } from "../lib/winSound";
import { useRealtime } from "../realtime/RealtimeProvider";

const CHIPS = [10, 50, 100, 500, 1000];
/** Cuanto dura el "rodar" del numero antes de mostrar el resultado (con tiempo de sobra para que llegue la respuesta del servidor). */
const ROLL_MS = 900;

const prefersReducedMotion = () =>
  typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Lo que se muestra al terminar una tirada, venga del aviso en vivo o de la consulta de respaldo. */
interface Result {
  betId: string;
  status: "Settled" | "Rejected" | "Voided";
  roll: number | null;
  stake: number;
  payout: number;
  failureReason: string | null;
}

const fromNotice = (n: RoundClosedNotice): Result => ({
  betId: n.betId,
  status: n.status,
  roll: n.winningNumber,
  stake: n.stake,
  payout: n.payout,
  failureReason: n.failureReason,
});
const fromRound = (r: DiceRound): Result => ({
  betId: r.betId,
  status: r.status as Result["status"],
  roll: r.roll,
  stake: r.stake,
  payout: r.status === "Settled" ? (r.payout ?? 0) : 0,
  failureReason: r.failureReason,
});

export function Dice() {
  const { balance, onRoundClosed, holdBalance } = useRealtime();
  const toasts = useToasts();
  const queryClient = useQueryClient();
  const place = usePlaceDice();
  const recent = useDiceRounds(10);

  const [direction, setDirection] = useState<DiceDirection>("Under");
  const [chance, setChance] = useState(4950);
  const [stake, setStake] = useState(10);
  const [waitingFor, setWaitingFor] = useState<string | null>(null);
  const [rolling, setRolling] = useState(false);
  const [shown, setShown] = useState<number | null>(null);
  const [result, setResult] = useState<Result | null>(null);
  const [winPopup, setWinPopup] = useState<Result | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Misma apuesta = misma clave de idempotencia: si la respuesta se pierde y el jugador reintenta, el servidor no cobra dos veces.
  const pending = useRef<{ fingerprint: string; key: string } | null>(null);
  const closedRef = useRef<Result | null>(null);
  const rollDoneRef = useRef(false);
  const releaseRef = useRef<(() => void) | null>(null);
  const timers = useRef<number[]>([]);

  const release = () => {
    releaseRef.current?.();
    releaseRef.current = null;
  };
  useEffect(() => () => {
    releaseRef.current?.();
    timers.current.forEach((t) => window.clearInterval(t));
  }, []);

  const stakeOk = Number.isInteger(stake) && stake >= MIN_STAKE && stake <= MAX_STAKE;
  const canBet = stakeOk && stake <= balance.available && !rolling && !place.isPending;
  const potential = payoutFor(stake, chance);

  const reveal = (closed: Result) => {
    setResult(closed);
    setRolling(false);
    setWaitingFor(null);
    setShown(closed.roll);
    release(); // recien ahora el saldo se actualiza
    void queryClient.invalidateQueries({ queryKey: ["dice"] });
    if (closed.status === "Settled") {
      if (closed.payout > closed.stake) {
        setWinPopup(closed);
        playWinSound();
      } else if (closed.payout > 0) toasts.show("info", `Salió ${formatHundredths(closed.roll ?? 0)}: recuperaste tus fichas.`);
      else toasts.show("loss", `Salió ${formatHundredths(closed.roll ?? 0)}.`);
    } else if (closed.status === "Rejected") toasts.show("error", failureMessage(closed.failureReason));
    else toasts.show("info", "La tirada se anuló y tus fichas volvieron a tu saldo.");
  };

  /** El resultado se muestra cuando el numero termino de rodar Y la ronda ya esta cobrada (lo que llegue ultimo). */
  const tryReveal = () => {
    const closed = closedRef.current;
    if (!closed || !rollDoneRef.current) return;
    closedRef.current = null;
    reveal(closed);
  };

  const onClosed = (closed: Result) => {
    if (closedRef.current || closed.betId !== waitingFor) return;
    closedRef.current = closed;
    tryReveal();
  };
  const onClosedRef = useRef(onClosed);
  useEffect(() => {
    onClosedRef.current = onClosed;
  });

  // Aviso en vivo.
  useEffect(() => onRoundClosed((notice) => onClosedRef.current(fromNotice(notice))), [onRoundClosed]);

  // Respaldo: si el aviso se perdio, la consulta periodica cierra igual la tirada.
  const polled = useDiceRound(waitingFor);
  useEffect(() => {
    const round = polled.data;
    if (round && ["Settled", "Rejected", "Voided"].includes(round.status)) onClosedRef.current(fromRound(round));
  }, [polled.data]);

  const startRolling = () => {
    rollDoneRef.current = false;
    if (prefersReducedMotion()) {
      rollDoneRef.current = true;
      return;
    }
    const spin = window.setInterval(() => setShown(Math.floor(Math.random() * DICE_ROLLS)), 60);
    timers.current.push(spin);
    window.setTimeout(() => {
      window.clearInterval(spin);
      rollDoneRef.current = true;
      tryReveal();
    }, ROLL_MS);
  };

  const submit = async () => {
    setError(null);
    const fingerprint = `${direction}:${chance}:${stake}`;
    if (pending.current?.fingerprint !== fingerprint) pending.current = { fingerprint, key: crypto.randomUUID() };

    // El numero empieza a rodar ya, sin conocer el resultado, y el saldo se congela hasta que se muestre.
    setResult(null);
    setRolling(true);
    closedRef.current = null;
    releaseRef.current = holdBalance();
    startRolling();

    try {
      const accepted = await place.mutateAsync({ direction, chance, stake, idempotencyKey: pending.current.key });
      pending.current = null; // aceptada: la proxima apuesta usa otra clave
      setWaitingFor(accepted.betId);
    } catch (e) {
      timers.current.forEach((t) => window.clearInterval(t));
      setRolling(false);
      setShown(null);
      release();
      setError(errorMessage(e));
      // Error definitivo (la API lo rechazo): proxima apuesta con clave nueva. Si fue de red, se conserva para reintentar sin duplicar.
      if (e instanceof Error && "status" in e && (e as { status: number }).status !== 0) pending.current = null;
    }
  };

  const won = result?.status === "Settled" && result.payout > 0;
  const numberClass = `dice-number ${rolling ? "dice-number--rolling" : result?.status === "Settled" ? (won ? "dice-number--win" : "dice-number--loss") : ""}`;

  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Dados</p>
        <h1 className="display">Elegí tu probabilidad</h1>
        <p className="muted">El servidor sortea un número de 0,00 a 99,99. Cuanto menos probable, más paga. Cada tirada se puede verificar.</p>
      </header>

      <div className="table-grid">
        <section className="card" aria-labelledby="apuesta">
          <h2 id="apuesta" className="section-title">1. Elegí cómo apostar</h2>

          <div className="chips" role="radiogroup" aria-label="Dirección">
            {(["Under", "Over"] as const).map((d) => (
              <button key={d} type="button" role="radio" aria-checked={direction === d} disabled={rolling}
                className={`choice ${direction === d ? "choice--on" : ""}`} onClick={() => setDirection(d)}>
                {d === "Under" ? "Por debajo" : "Por encima"}
              </button>
            ))}
          </div>

          <label className="dice-chance">
            <span>Probabilidad de ganar: <strong>{formatHundredths(chance)} %</strong></span>
            <input type="range" min={MIN_CHANCE} max={MAX_CHANCE} step={50} value={chance} disabled={rolling}
              aria-label="Probabilidad de ganar" onChange={(e) => setChance(Number(e.target.value))} />
          </label>
          <p className="muted">
            Ganás si sale <strong>{limitLabel(direction, chance)}</strong>. Paga <strong>x{multiplierLabel(chance)}</strong>.
          </p>

          <h2 className="section-title">2. ¿Cuántas fichas?</h2>
          <StakePicker stake={stake} onChange={setStake} chips={CHIPS} min={MIN_STAKE} max={MAX_STAKE} disabled={rolling} defaultStake={10} />
          {stakeOk && <p className="muted">Si ganás, cobrás <strong>{formatChips(potential)}</strong> fichas.</p>}

          {stakeOk && stake > balance.available && balance.ready && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa apuesta.</p>}
          {error && <p className="notice notice--error" role="alert">{error}</p>}

          <div className="actions dock">
            <button type="button" className="btn btn--gold btn--lg" disabled={!canBet} onClick={() => void submit()}>
              {rolling ? "Tirando…" : place.isPending ? "Enviando…" : `Tirar por ${formatChips(stake)} fichas`}
            </button>
          </div>
        </section>

        <section className="card result" aria-labelledby="resultado" aria-live="polite">
          <h2 id="resultado" className="section-title">El dado</h2>
          <p className={numberClass} data-testid="dice-number">{shown === null ? "–" : formatHundredths(shown)}</p>
          {!rolling && !result && <p className="muted">Tu próxima tirada aparece acá.</p>}
          {rolling && <p role="status">Tirando el dado…</p>}
          {!rolling && result && <ResultView result={result} direction={direction} chance={chance} />}

          <h3 className="section-subtitle">Últimas tiradas</h3>
          <div className="strip">
            {recent.data?.filter((r) => r.roll !== null && r.status === "Settled" && !(rolling && r.betId === waitingFor)).slice(0, 10).map((r) => (
              <span key={r.betId} className={`dice-chip ${wins(r.direction, r.chance, r.roll!) ? "dice-chip--win" : ""}`}>{formatHundredths(r.roll!)}</span>
            ))}
            {recent.data?.every((r) => r.roll === null) && <span className="muted">Sin tiradas todavía.</span>}
          </div>
        </section>
      </div>

      {winPopup && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWinPopup(null)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <p className="dice-number dice-number--win">{formatHundredths(winPopup.roll ?? 0)}</p>
            <h2 id="win-title" className="win-modal__title">¡Ganaste!</h2>
            <p className="win-modal__amount">{formatChips(winPopup.payout)} fichas</p>
            <p className="muted">Apostaste {formatChips(winPopup.stake)}.</p>
            <button type="button" className="btn btn--gold" autoFocus onClick={() => setWinPopup(null)}>
              Continuar
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function ResultView({ result, direction, chance }: { result: Result; direction: DiceDirection; chance: number }) {
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
        <p className="outcome__title">Tirada anulada</p>
        <p>Tus fichas volvieron a tu saldo.</p>
      </div>
    );
  }

  const won = result.payout > 0;
  return (
    <div className={`outcome ${won ? "outcome--win" : "outcome--loss"}`}>
      <p className="outcome__title">{won ? `Cobraste ${formatChips(result.payout)} fichas` : "No hubo suerte esta vez"}</p>
      <p className="muted">
        Salió {formatHundredths(result.roll ?? 0)} y necesitabas {limitLabel(direction, chance)}. Apostaste {formatChips(result.stake)}.
      </p>
    </div>
  );
}
