import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useCancelWithdrawal, useMyWithdrawals, useRequestWithdrawal } from "../api/hooks";
import type { WithdrawalStatus } from "../api/types";
import { useToasts } from "../components/Toasts";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";
import { useRealtime } from "../realtime/RealtimeProvider";

/** Retiro minimo por defecto del servidor; el servidor es quien manda (si cambia, responde el error). */
const MIN_WITHDRAWAL = 10;

export const WITHDRAWAL_LABELS: Record<WithdrawalStatus, string> = {
  Pending: "Esperando a tu cajero",
  Paid: "Cobrado",
  Rejected: "Rechazado: las fichas volvieron",
  Cancelled: "Cancelado: las fichas volvieron",
  Expired: "Venció: las fichas volvieron",
};

/** Retirar fichas: se apartan hasta que tu cajero las cobra (o las rechazás, cancelás o vence el plazo y vuelven). */
export function Withdraw() {
  const { balance } = useRealtime();
  const mine = useMyWithdrawals();
  const request = useRequestWithdrawal();
  const cancel = useCancelWithdrawal();
  const toasts = useToasts();
  const [amount, setAmount] = useState("");
  // Una clave por intento: si el envio se repite (doble clic, reintento), el servidor no aparta las fichas dos veces.
  const [attemptKey, setAttemptKey] = useState(() => crypto.randomUUID());

  const value = Number(amount);
  const valid = Number.isInteger(value) && value >= MIN_WITHDRAWAL;
  const enough = !balance.ready || value <= balance.available;
  const canSubmit = valid && enough && !request.isPending;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit) return;
    request.mutate(
      { amount: value, idempotencyKey: attemptKey },
      {
        onSuccess: () => {
          toasts.show("info", `Pediste retirar ${formatChips(value)} fichas: quedan apartadas hasta que tu cajero las cobre.`);
          setAmount("");
          setAttemptKey(crypto.randomUUID());
        },
        onError: (error) => toasts.show("error", error instanceof ApiError && error.title === "InsufficientFunds" ? "No te alcanzan las fichas para ese retiro." : errorMessage(error)),
      },
    );
  };

  return (
    <div className="stack">
      <section className="card" aria-labelledby="retiro-titulo">
        <p className="eyebrow">Retirar</p>
        <h1 id="retiro-titulo" className="display">Retirar fichas</h1>
        <p className="muted">
          Pedí cuántas fichas querés retirar y se <strong>apartan</strong> de tu saldo (no las podés jugar) hasta que tu cajero las cobre. Si no las cobra, podés cancelar el pedido y te vuelven; si nadie lo atiende a
          tiempo, también vuelven solas. Si no tenés cajero, lo atiende el administrador.
        </p>
        <form onSubmit={submit} className="stack">
          <label className="field">
            <span>Fichas a retirar (mínimo {MIN_WITHDRAWAL})</span>
            <input type="number" inputMode="numeric" min={MIN_WITHDRAWAL} step={1} value={amount} onChange={(e) => setAmount(e.target.value)} />
          </label>
          {valid && !enough && <p className="notice notice--error" role="alert">No te alcanzan las fichas para ese retiro.</p>}
          <button type="submit" className="btn btn--gold" disabled={!canSubmit}>
            {request.isPending ? "Pidiendo…" : "Pedir retiro"}
          </button>
        </form>
      </section>

      <section className="card" aria-labelledby="retiro-lista">
        <h2 id="retiro-lista">Tus pedidos</h2>
        {mine.isError && <p className="notice notice--error" role="alert">No pudimos cargar tus pedidos.</p>}
        {mine.data?.length === 0 && <p className="muted">Todavía no pediste ningún retiro.</p>}
        <ul className="admin-users">
          {mine.data?.map((w) => (
            <li key={w.id} className="admin-user">
              <span className="admin-user__id">{formatChips(w.amount)} fichas</span>
              <span className="muted">{formatDateTime(w.createdAt)}</span>
              <span>{WITHDRAWAL_LABELS[w.status] ?? w.status}</span>
              {w.status === "Pending" && (
                <button
                  type="button"
                  className="btn btn--ghost"
                  disabled={cancel.isPending}
                  onClick={() => cancel.mutate(w.id, { onSuccess: () => toasts.show("info", "Cancelaste el retiro: las fichas volvieron."), onError: (error) => toasts.show("error", errorMessage(error)) })}
                >
                  Cancelar
                </button>
              )}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
