import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { queryKeys, useCrashBets, useCrashCashOut, useCrashState, usePlaceCrashBet } from "../api/hooks";
import type { CrashBet, CrashRound } from "../api/types";
import { Confetti } from "../components/Confetti";
import { CrashCanvas } from "../components/CrashCanvas";
import { useToasts } from "../components/Toasts";
import { StakePicker } from "../components/StakePicker";
import { crashView, formatMultiplier, parseAutoCashOut, type RoundVerification, verifyRound } from "../lib/crash";
import { formatChips } from "../lib/format";
import { errorMessage, failureMessage } from "../lib/messages";
import { playWinSound } from "../lib/winSound";
import { useRealtime } from "../realtime/RealtimeProvider";

const CHIPS = [10, 50, 100, 500];

/** Crash: un cohete sube con un multiplicador y explota cuando decide el servidor; hay que retirar antes. La ronda es compartida por todos los jugadores. */
export function Crash() {
  const state = useCrashState();
  const { balance, connection, onGameEvent } = useRealtime();
  const queryClient = useQueryClient();
  const toasts = useToasts();
  const placeBet = usePlaceCrashBet();
  const cashOut = useCrashCashOut();
  const recent = useCrashBets(5);

  const data = state.data;
  const round = data?.round ?? null;
  const bet = data?.myBet ?? null;

  const [stake, setStake] = useState(10);
  const [auto, setAuto] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [win, setWin] = useState<CrashBet | null>(null);
  const [verification, setVerification] = useState<{ roundId: string; result: RoundVerification | null } | null>(null);
  const pending = useRef<{ fingerprint: string; key: string } | null>(null);
  const watched = useRef(new Set<string>());

  // El reloj del navegador se corrige con la hora del servidor para dibujar el multiplicador a tiempo.
  const roundRef = useRef<CrashRound | null>(null);
  const offsetRef = useRef(0);
  useEffect(() => {
    roundRef.current = round;
    offsetRef.current = data?.offsetMs ?? 0;
  });
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 100);
    return () => window.clearInterval(id);
  }, []);

  // Cada hecho en vivo (se abrio, empezo, exploto) refresca el estado al instante; la consulta cada segundo es solo respaldo.
  useEffect(
    () =>
      onGameEvent((event) => {
        if (event.game === "crash") void queryClient.invalidateQueries({ queryKey: queryKeys.crashState });
      }),
    [onGameEvent, queryClient],
  );

  const view = crashView(round, now + (data?.offsetMs ?? 0));
  const myBetInThisRound = bet !== null && round !== null && bet.roundId === round.id && bet.status !== "Rejected";

  // Se festeja solo una victoria que se vio resolverse en esta pantalla (no una vieja que quedo del estado al entrar).
  useEffect(() => {
    if (!bet) return;
    if (bet.status === "Placed" || bet.status === "Resolved") {
      watched.current.add(bet.betId);
    } else if (bet.status === "Settled" && (bet.payout ?? 0) > bet.stake && watched.current.delete(bet.betId)) {
      setWin(bet);
      playWinSound();
      void queryClient.invalidateQueries({ queryKey: queryKeys.crashBets });
    }
  }, [bet, queryClient]);

  const autoValue = auto.trim() === "" ? null : parseAutoCashOut(auto);
  const autoValid = auto.trim() === "" || autoValue !== null;
  const stakeValid = Number.isInteger(stake) && stake >= (data?.minStake ?? 1) && stake <= (data?.maxStake ?? 10_000);
  // Sin tiempo real conectado no se aceptan apuestas: el retiro depende de ver la ronda en vivo y no se puede perder fichas sin poder cobrar.
  const live = connection === "connected";
  const canBet = view.phase === "betting" && view.secondsLeft > 0.3 && live && !myBetInThisRound && stakeValid && autoValid && stake <= balance.available && !placeBet.isPending;
  // La apuesta en juego siempre tiene su boton de retiro: si el reloj ya paso el cierre pero el estado todavia dice "Betting", el cohete esta despegando.
  const liftingOff = view.phase === "betting" && view.secondsLeft <= 0;
  const canWithdraw = bet?.inPlay === true && (view.phase === "running" || liftingOff);

  const submit = async () => {
    setError(null);
    const fingerprint = `${stake}|${autoValue ?? ""}`;
    if (pending.current?.fingerprint !== fingerprint) pending.current = { fingerprint, key: crypto.randomUUID() };
    try {
      await placeBet.mutateAsync({ stake, autoCashOut: autoValue, idempotencyKey: pending.current.key });
      pending.current = null; // aceptada: la proxima apuesta usa otra clave
      void queryClient.invalidateQueries({ queryKey: queryKeys.crashState });
    } catch (e) {
      setError(errorMessage(e));
      // Error definitivo: proxima apuesta con clave nueva. Si fue de red, se conserva para reintentar sin duplicar.
      if (e instanceof Error && "status" in e && (e as { status: number }).status !== 0) pending.current = null;
    }
  };

  const withdraw = () => {
    if (!bet) return;
    setError(null);
    cashOut.mutate(bet.betId, {
      onSuccess: (result) => {
        toasts.show("win", `Retiraste en ${formatMultiplier(result.multiplier)} y cobrás ${formatChips(result.payout)} fichas.`);
        void queryClient.invalidateQueries({ queryKey: queryKeys.crashState });
      },
      onError: (e) => {
        setError(errorMessage(e));
        void queryClient.invalidateQueries({ queryKey: queryKeys.crashState });
      },
    });
  };

  const lastCrash = data?.history[0] ?? null;
  const potential = bet ? Math.floor((bet.stake * view.multiplier) / 100) : 0;
  const statusLabel =
    view.phase === "betting" ? `Apuestas abiertas: cierran en ${Math.ceil(view.secondsLeft)} s`
    : view.phase === "running" ? "El cohete está subiendo"
    : view.phase === "crashed" ? `¡Explotó en ${formatMultiplier(view.multiplier)}!`
    : view.phase === "aborted" ? "La ronda se cortó"
    : "Esperando la próxima ronda…";

  return (
    <div className="stack">
      <header>
        <p className="eyebrow">Crash</p>
        <h1 className="display">Retirá antes de que explote</h1>
        <p className="muted">El multiplicador sube y el servidor decide cuándo explota. Cada ronda se compromete con un hash antes de apostar y se puede verificar.</p>
      </header>

      <div className="table-grid">
        <section className="card" aria-labelledby="cohete">
          <h2 id="cohete" className="section-title">La ronda</h2>
          <div className="crash-stage" data-phase={view.phase} data-testid="crash-stage">
            <CrashCanvas
              growthPerSecond={round?.growthPerSecond ?? data?.growthPerSecond ?? 0.07}
              getView={() => crashView(roundRef.current, Date.now() + offsetRef.current)}
              label={statusLabel}
            />
            <p className={`crash-multiplier crash-multiplier--${view.phase}`} aria-hidden="true" data-testid="crash-multiplier">
              {view.phase === "betting" || view.phase === "waiting" || view.phase === "aborted" ? "—" : formatMultiplier(view.multiplier)}
            </p>
          </div>
          <p role="status" className="crash-status">{statusLabel}</p>

          <h3 className="section-subtitle">Últimas explosiones</h3>
          <ul className="crash-history" aria-label="Últimas explosiones">
            {data?.history.map((r) => (
              <li key={r.id} className={`crash-pill ${(r.crashPoint ?? 0) >= 200 ? "crash-pill--high" : "crash-pill--low"}`}>{formatMultiplier(r.crashPoint ?? 100)}</li>
            ))}
            {data?.history.length === 0 && <li className="muted">Todavía no explotó ninguna.</li>}
          </ul>
        </section>

        <section className="card" aria-labelledby="apuesta">
          <h2 id="apuesta" className="section-title">Tu apuesta</h2>
          <StakePicker stake={stake} onChange={setStake} chips={CHIPS} min={data?.minStake ?? 1} max={data?.maxStake ?? 10_000} defaultStake={10} disabled={myBetInThisRound} />
          <label className="field">
            <span>Retiro automático en (opcional, de 1,01 a 100)</span>
            <input type="text" inputMode="decimal" placeholder="por ejemplo 2,5" value={auto} disabled={myBetInThisRound} aria-invalid={!autoValid} onChange={(e) => setAuto(e.target.value)} />
          </label>

          {!autoValid && <p className="notice notice--error" role="alert">El retiro automático tiene que estar entre 1,01 y 100.</p>}
          {stake > balance.available && balance.ready && stakeValid && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa apuesta.</p>}
          {!live && view.phase === "betting" && !myBetInThisRound && <p className="notice notice--error" role="alert">Sin conexión en vivo no se puede apostar: reconectando…</p>}
          {error && <p className="notice notice--error" role="alert">{error}</p>}

          <div className="dock">
            {canWithdraw || (bet?.inPlay && view.phase === "betting") ? (
              <button type="button" className="btn btn--gold btn--lg btn--block" disabled={cashOut.isPending || !canWithdraw} onClick={withdraw}>
                {cashOut.isPending ? "Retirando…" : !canWithdraw ? "Retirar (disponible al despegar)" : `Retirar ${formatChips(potential)} fichas (${formatMultiplier(view.multiplier)})`}
              </button>
            ) : (
              <button type="button" className="btn btn--gold btn--lg btn--block" disabled={!canBet} onClick={() => void submit()}>
                {placeBet.isPending ? "Enviando…" : myBetInThisRound ? "Apuesta hecha" : `Apostar ${formatChips(stake)} fichas`}
              </button>
            )}
          </div>

          {bet && myBetInThisRound && <BetStatus bet={bet} />}
          {bet?.status === "Rejected" && bet.roundId === round?.id && <p className="notice notice--error" role="alert">{failureMessage(bet.failureReason)}</p>}

          <h3 className="section-subtitle">Tus últimas apuestas</h3>
          <ul className="spin-list" aria-label="Tus últimas apuestas">
            {recent.data?.filter((b) => b.status === "Settled" || b.status === "Rejected" || b.status === "Voided").map((b) => (
              <li key={b.betId}>
                <span>{formatChips(b.stake)} fichas{b.cashedOutAt ? ` · retiro en ${formatMultiplier(b.cashedOutAt)}` : ""}</span>
                <span className={(b.payout ?? 0) > b.stake ? "win" : "muted"}>{b.status === "Settled" ? ((b.payout ?? 0) > 0 ? `+${formatChips(b.payout ?? 0)}` : "perdida") : b.status === "Rejected" ? "rechazada" : "anulada"}</span>
              </li>
            ))}
            {recent.data?.length === 0 && <li className="muted">Todavía no apostaste en Crash.</li>}
          </ul>
        </section>
      </div>

      <section className="card" aria-labelledby="verificar">
        <h2 id="verificar" className="section-title">Verificá la última ronda</h2>
        {lastCrash ? (
          <>
            <p className="muted">
              Antes de apostar el servidor publicó el compromiso (el hash de su semilla). Al explotar reveló la semilla: con ella podés recalcular el punto de explosión vos mismo.
            </p>
            <dl className="crash-proof">
              <dt>Compromiso</dt>
              <dd className="mono">{lastCrash.commitment}</dd>
              <dt>Semilla revelada</dt>
              <dd className="mono">{lastCrash.serverSeed}</dd>
              <dt>Explotó en</dt>
              <dd>{formatMultiplier(lastCrash.crashPoint ?? 100)}</dd>
            </dl>
            <button type="button" className="btn btn--ghost" onClick={() => void verifyRound(lastCrash).then((result) => setVerification({ roundId: lastCrash.id, result }))}>
              Verificar en mi navegador
            </button>
            {verification?.roundId === lastCrash.id && verification.result && (
              <p role="status" className={verification.result.commitmentOk && verification.result.crashPointOk ? "notice" : "notice notice--error"}>
                {verification.result.commitmentOk ? "✓ La semilla coincide con el compromiso." : "✗ La semilla NO coincide con el compromiso."}{" "}
                {verification.result.crashPointOk ? `✓ El punto recalculado (${formatMultiplier(verification.result.computedCrashPoint)}) coincide.` : `✗ El punto recalculado es ${formatMultiplier(verification.result.computedCrashPoint)}.`}
              </p>
            )}
          </>
        ) : (
          <p className="muted">Cuando explote la primera ronda vas a poder verificarla acá.</p>
        )}
      </section>

      {win && (
        <div className="win-modal" role="dialog" aria-modal="true" aria-labelledby="win-title" onClick={() => setWin(null)}>
          <Confetti />
          <div className="win-modal__box" onClick={(e) => e.stopPropagation()}>
            <p className="crash-multiplier crash-multiplier--running" aria-hidden="true">{win.cashedOutAt ? formatMultiplier(win.cashedOutAt) : "🚀"}</p>
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

function BetStatus({ bet }: { bet: CrashBet }) {
  const auto = bet.autoCashOut ? ` · retiro automático en ${formatMultiplier(bet.autoCashOut)}` : "";
  if (bet.status === "Placed" && !bet.inPlay) return <p className="muted" role="status">Reservando tus fichas…{auto}</p>;
  if (bet.inPlay) return <p role="status">Tu apuesta de {formatChips(bet.stake)} fichas está en juego{auto}.</p>;
  if (bet.failureReason === "BettingClosed") return <p className="muted" role="status">Tu apuesta llegó tarde: te devolvimos las fichas.</p>;
  if (bet.failureReason === "RoundAborted") return <p className="muted" role="status">La ronda se cortó: te devolvimos las fichas.</p>;
  if (bet.status === "Voided") return <p className="muted" role="status">La apuesta se anuló y tus fichas volvieron a tu saldo.</p>;
  if (bet.cashedOutAt) return <p role="status">Retiraste en {formatMultiplier(bet.cashedOutAt)}: {formatChips(bet.payout ?? 0)} fichas.</p>;
  return <p className="muted" role="status">{(bet.payout ?? 0) === 0 ? "El cohete explotó antes de que retiraras." : "Resultado cargándose…"}</p>;
}
