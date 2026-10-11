import { useRef, useState, type FormEvent, type ReactNode } from "react";
import { ApiError } from "../api/client";
import { useCreateTable, useJoinByCode, useJoinTable, useLeaveTable, useStartTable, useTable, useTableBots, useTableRules, useTables } from "../api/hooks";
import type { TableRules, TableState, TableSummary } from "../api/types";
import { formatChips } from "../lib/format";
import { detailedErrorMessage } from "../lib/messages";
import { useRealtime } from "../realtime/RealtimeProvider";

/** El estado de una mesa con el desfase del reloj del servidor (lo que devuelve `useTable`). */
export type LiveTable<G> = TableState<G> & { offsetMs: number };

export interface TablesLobbyProps<G> {
  /** El id del juego en la API: las mesas viven bajo /games/{gameId}. */
  gameId: string;
  /** Como se llama el juego en los textos ("Uno"). */
  gameName: string;
  /** La partida en si (cuando la mesa esta `Playing` o `Finished`): la dibuja cada juego. `leave` vuelve a la lista de mesas. */
  renderGame: (table: LiveTable<G>, leave: () => void) => ReactNode;
}

/**
 * Mesas entre jugadores, comun a todos los juegos de mesa (Uno, y despues Truco y Poker): la lista de mesas abiertas, crear una, unirse (con codigo si es
 * privada), la sala de espera y, ya empezada, la partida de cada juego (`renderGame`). Solo se puede estar sentado en UNA mesa activa por juego: si ya
 * estas sentado, el lobby te lleva directo a ella.
 */
export function TablesLobby<G>({ gameId, gameName, renderGame }: TablesLobbyProps<G>) {
  const tables = useTables(gameId);
  const [activeId, setActiveId] = useState<string | null>(null);
  // La mesa que se acaba de dejar: el listado puede tardar un instante en dejar de mostrarla como propia.
  const [dismissedId, setDismissedId] = useState<string | null>(null);

  const mineActive = tables.data?.find((t) => t.mine && (t.status === "Open" || t.status === "Playing") && t.id !== dismissedId) ?? null;
  // Una vez dentro de una mesa se queda ahi hasta que el jugador sale: al terminar la partida el listado ya no la trae, pero hay que ver el resultado.
  if (activeId === null && mineActive !== null) setActiveId(mineActive.id);
  const currentId = activeId ?? mineActive?.id ?? null;

  const enter = (id: string) => {
    setDismissedId(null);
    setActiveId(id);
  };
  const leave = () => {
    setDismissedId(currentId);
    setActiveId(null);
  };

  if (currentId !== null) {
    return <TableRoom<G> key={currentId} gameId={gameId} gameName={gameName} tableId={currentId} renderGame={renderGame} onExit={leave} />;
  }
  return <TableList gameId={gameId} gameName={gameName} tables={tables.data} loading={tables.isLoading} failed={tables.isError} onEnter={enter} />;
}

const STEPS = ["Creá una mesa o unite a una abierta", "Sumá bots o esperá a otros jugadores", "El dueño toca Empezar y se reparte"];

/** Los tres pasos de una partida entre jugadores, con el actual resaltado: guia a quien no sabe por donde empezar. */
function HowItWorks({ current }: { current: 1 | 2 | 3 }) {
  return (
    <ol className="tl-steps" aria-label="Cómo se juega en mesa">
      {STEPS.map((text, i) => (
        <li key={text} className={`tl-step ${i + 1 === current ? "tl-step--now" : ""} ${i + 1 < current ? "tl-step--done" : ""}`} aria-current={i + 1 === current ? "step" : undefined}>
          <span className="tl-step__n" aria-hidden="true">{i + 1 < current ? "✓" : i + 1}</span>
          <span>{text}</span>
        </li>
      ))}
    </ol>
  );
}

const plural = (n: number, one: string, many: string): string => `${n} ${n === 1 ? one : many}`;

// ---- Lista de mesas ----

function TableList({ gameId, gameName, tables, loading, failed, onEnter }: { gameId: string; gameName: string; tables: TableSummary[] | undefined; loading: boolean; failed: boolean; onEnter: (id: string) => void }) {
  const rules = useTableRules(gameId);
  const join = useJoinTable(gameId);
  const joinByCode = useJoinByCode(gameId);
  const [error, setError] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [codeError, setCodeError] = useState<string | null>(null);
  const { balance } = useRealtime();

  const open = (tables ?? []).filter((t) => t.status === "Open");

  const sit = (table: TableSummary) => {
    setError(null);
    join.mutate({ tableId: table.id }, { onSuccess: () => onEnter(table.id), onError: (e) => setError(detailedErrorMessage(e)) });
  };

  const submitCode = (event: FormEvent) => {
    event.preventDefault();
    setCodeError(null);
    const trimmed = code.trim();
    if (trimmed.length !== 6) {
      setCodeError("El código tiene 6 caracteres.");
      return;
    }
    joinByCode.mutate(trimmed, { onSuccess: (joined) => onEnter(joined.tableId), onError: (e) => setCodeError(detailedErrorMessage(e)) });
  };

  return (
    <div className="stack">
      <HowItWorks current={1} />
      <section className="card tl-list" aria-labelledby="tl-mesas">
        <h2 id="tl-mesas" className="section-title">Mesas abiertas</h2>
        {loading && <p className="muted" role="status">Cargando las mesas…</p>}
        {failed && <p className="notice notice--error" role="alert">No pudimos cargar las mesas.</p>}
        {!loading && !failed && open.length === 0 && <p className="muted">No hay mesas abiertas. Creá la primera y sumá un bot para jugar enseguida.</p>}
        <ul className="tl-tables" aria-label="Mesas abiertas">
          {open.map((table) => (
            <li key={table.id} className="tl-table">
              <div>
                <p className="tl-table__name">{table.name}</p>
                <p className="muted tl-table__meta">
                  Entrada de {formatChips(table.buyIn)} fichas · {table.players}/{table.maxPlayers} jugadores{table.bots > 0 ? ` (${plural(table.bots, "bot", "bots")})` : ""}
                </p>
              </div>
              <button
                type="button"
                className="btn btn--gold"
                aria-label={`Unirme a ${table.name}`}
                disabled={join.isPending || table.players >= table.maxPlayers || (balance.ready && table.buyIn > balance.available)}
                onClick={() => sit(table)}
              >
                {table.players >= table.maxPlayers ? "Llena" : "Unirme"}
              </button>
            </li>
          ))}
        </ul>
        {error && <p className="notice notice--error" role="alert">{error}</p>}

        <form className="tl-code" onSubmit={submitCode}>
          <label className="field">
            <span>Tengo un código</span>
            <input type="text" value={code} autoComplete="off" spellCheck={false} placeholder="6 caracteres" maxLength={6} onChange={(e) => setCode(e.target.value)} />
          </label>
          <button type="submit" className="btn btn--ghost" disabled={joinByCode.isPending || code.trim() === ""}>Entrar con el código</button>
        </form>
        {codeError && <p className="notice notice--error" role="alert">{codeError}</p>}
      </section>

      <CreateTable gameId={gameId} gameName={gameName} rules={rules.data} rulesFailed={rules.isError} onCreated={onEnter} />
    </div>
  );
}

// ---- Crear mesa ----

function CreateTable({ gameId, gameName, rules, rulesFailed, onCreated }: { gameId: string; gameName: string; rules: TableRules | undefined; rulesFailed: boolean; onCreated: (id: string) => void }) {
  const create = useCreateTable(gameId);
  const { balance } = useRealtime();
  const [buyIn, setBuyIn] = useState<number | null>(null);
  const [players, setPlayers] = useState<number | null>(null);
  const [isPrivate, setIsPrivate] = useState(false);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const pending = useRef<{ fingerprint: string; key: string } | null>(null);

  if (rulesFailed) return <p className="notice notice--error" role="alert">No pudimos cargar las reglas de {gameName}.</p>;
  if (!rules) return null;

  const entry = buyIn ?? Math.min(rules.maxBuyIn, Math.max(rules.minBuyIn, 10));
  const seats = players ?? rules.minPlayers;
  const entryValid = Number.isInteger(entry) && entry >= rules.minBuyIn && entry <= rules.maxBuyIn;
  const affordable = !balance.ready || entry <= balance.available;
  const options = Array.from({ length: rules.maxPlayers - rules.minPlayers + 1 }, (_, i) => rules.minPlayers + i);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const body = { buyIn: entry, maxPlayers: seats, isPrivate, name: name.trim() || undefined };
    const fingerprint = JSON.stringify(body);
    if (pending.current?.fingerprint !== fingerprint) pending.current = { fingerprint, key: crypto.randomUUID() };
    try {
      const created = await create.mutateAsync({ body, idempotencyKey: pending.current.key });
      pending.current = null;
      onCreated(created.tableId);
    } catch (e) {
      setError(detailedErrorMessage(e));
      // Error definitivo: la proxima vez, clave nueva. Si fue de red, se conserva para reintentar sin crear dos mesas.
      if (e instanceof ApiError && e.status !== 0) pending.current = null;
    }
  };

  return (
    <section className="card" aria-labelledby="tl-crear">
      <h2 id="tl-crear" className="section-title">Crear mesa</h2>
      <p className="muted tl-intro">¿No hay una mesa para vos? Armá la tuya: elegí cuántas fichas pone cada uno y cuántos juegan. Después la abrís a otros jugadores o sumás bots.</p>
      <form id="tl-create-form" className="tl-form" onSubmit={(e) => void submit(e)}>
        <label className="field">
          <span>Nombre (opcional)</span>
          <input type="text" value={name} maxLength={40} placeholder={`Mesa de ${gameName}`} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="field">
          <span>Entrada (fichas)</span>
          <input type="number" inputMode="numeric" min={rules.minBuyIn} max={rules.maxBuyIn} step={1} value={entry} aria-invalid={!entryValid} aria-describedby="tl-entry-hint" onChange={(e) => setBuyIn(Math.trunc(Number(e.target.value)))} />
        </label>
        <p id="tl-entry-hint" className="tl-hint">Lo que pone cada jugador para sentarse. El ganador se lleva todo el pozo{entryValid ? `: con ${plural(seats, "jugador", "jugadores")}, ${formatChips(entry * seats)} fichas` : ""}.</p>
        <div className="field" role="radiogroup" aria-label="Jugadores">
          <span>Jugadores</span>
          <div className="chips">
            {options.map((n) => (
              <button key={n} type="button" role="radio" aria-checked={seats === n} className={`choice choice--stake ${seats === n ? "choice--on" : ""}`} onClick={() => setPlayers(n)}>
                {n}
              </button>
            ))}
          </div>
        </div>
        <p className="tl-hint">Cuántos asientos tiene la mesa; los que no ocupen personas los podés completar con bots.</p>
        <label className="tl-check">
          <input type="checkbox" checked={isPrivate} aria-describedby="tl-private-hint" onChange={(e) => setIsPrivate(e.target.checked)} />
          <span>Mesa privada (se entra con un código)</span>
        </label>
        <p id="tl-private-hint" className="tl-hint">{isPrivate ? "No aparece en la lista: te damos un código de 6 caracteres para pasarle a quien quieras que juegue." : "Aparece en la lista de mesas abiertas y cualquiera puede unirse."}</p>
        {!entryValid && <p className="notice notice--error" role="alert">La entrada va de {formatChips(rules.minBuyIn)} a {formatChips(rules.maxBuyIn)} fichas.</p>}
        {entryValid && !affordable && <p className="notice notice--error" role="alert">No te alcanzan las fichas para esa entrada.</p>}
        {error && <p className="notice notice--error" role="alert">{error}</p>}
      </form>
      {/* Pegado abajo (como en los juegos): el boton va fuera del formulario pero lo envia con `form`. */}
      <div className="dock">
        <button type="submit" form="tl-create-form" className="btn btn--gold btn--lg btn--block" disabled={!entryValid || !affordable || create.isPending}>
          {create.isPending ? "Creando…" : "Crear mesa"}
        </button>
      </div>
    </section>
  );
}

// ---- Una mesa propia ----

function TableRoom<G>({ gameId, gameName, tableId, renderGame, onExit }: { gameId: string; gameName: string; tableId: string; renderGame: TablesLobbyProps<G>["renderGame"]; onExit: () => void }) {
  const state = useTable<G>(gameId, tableId);
  const table = state.data;

  // Si la mesa deja de existir para mi (por ejemplo una privada de la que ya no soy parte), no hay nada que seguir consultando.
  const gone = state.error instanceof ApiError && state.error.status >= 400 && state.error.status < 500;
  if (gone) {
    return (
      <div className="stack">
        <p className="notice notice--error" role="alert">{detailedErrorMessage(state.error)}</p>
        <button type="button" className="btn btn--ghost" onClick={onExit}>Volver a las mesas</button>
      </div>
    );
  }
  if (!table) return <p className="muted" role="status">{state.isError ? "No pudimos cargar la mesa." : "Entrando a la mesa…"}</p>;

  if (table.status === "Playing" || table.status === "Finished") return <>{renderGame(table, onExit)}</>;

  if (table.status === "Cancelled") {
    return (
      <section className="card" aria-labelledby="tl-cancelada">
        <h2 id="tl-cancelada" className="section-title">{table.name}</h2>
        <p role="status">La mesa se canceló. Si habías puesto fichas, vuelven a tu saldo.</p>
        <button type="button" className="btn btn--gold" onClick={onExit}>Volver a las mesas</button>
      </section>
    );
  }

  if (table.mySeat === null) {
    return (
      <section className="card" aria-labelledby="tl-fuera">
        <h2 id="tl-fuera" className="section-title">{table.name}</h2>
        <p role="status">Ya no estás sentado en esta mesa.</p>
        <button type="button" className="btn btn--gold" onClick={onExit}>Volver a las mesas</button>
      </section>
    );
  }

  return <WaitingRoom gameId={gameId} gameName={gameName} table={table} onExit={onExit} />;
}

// ---- Sala de espera ----

/** Por que todavia no se puede empezar (null si se puede). */
export function startBlocker(table: Pick<TableState, "seats" | "minPlayers">): string | null {
  const missing = table.minPlayers - table.seats.length;
  if (missing > 0) return `Faltan ${plural(missing, "jugador", "jugadores")} para empezar (mínimo ${table.minPlayers}). Podés sumar bots.`;
  const waiting = table.seats.filter((s) => !s.ready).length;
  if (waiting > 0) return `Esperando que se confirmen las fichas de ${plural(waiting, "asiento", "asientos")}.`;
  return null;
}

function WaitingRoom({ gameId, gameName, table, onExit }: { gameId: string; gameName: string; table: TableState; onExit: () => void }) {
  const leave = useLeaveTable(gameId);
  const bots = useTableBots(gameId, table.id);
  const start = useStartTable(gameId);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const blocker = startBlocker(table);
  const botCount = table.seats.filter((s) => s.isBot).length;
  const free = Array.from({ length: Math.max(0, table.maxPlayers - table.seats.length) }, (_, i) => i);
  const busy = leave.isPending || bots.add.isPending || bots.remove.isPending || start.isPending;
  const fail = (e: unknown) => setError(detailedErrorMessage(e));

  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text).then(
      () => {
        setCopied(true);
        window.setTimeout(() => setCopied(false), 2_000);
      },
      () => undefined,
    );
  };

  return (
    <div className="stack">
    <HowItWorks current={table.seats.length < table.minPlayers || blocker !== null ? 2 : 3} />
    <section className="card tl-room" aria-labelledby="tl-sala">
      <h2 id="tl-sala" className="section-title">{table.name}</h2>
      <p className="muted">
        {gameName} · entrada de {formatChips(table.buyIn)} fichas · {table.seats.length}/{table.maxPlayers} jugadores{table.isPrivate ? " · mesa privada" : ""}
      </p>

      {table.isPrivate && table.joinCode && (
        <div className="tl-invite">
          <p>
            Código: <strong className="mono" data-testid="tl-code">{table.joinCode}</strong>
          </p>
          <div className="tl-invite__actions">
            <button type="button" className="btn btn--ghost" onClick={() => copy(table.joinCode!)}>{copied ? "¡Copiado!" : "Copiar código"}</button>
          </div>
          <p className="muted">Quien quiera entrar escribe el código en "Tengo un código".</p>
        </div>
      )}

      <ul className="tl-seats" aria-label="Asientos">
        {table.seats.map((seat) => (
          <li key={seat.seat} className={`tl-seat ${seat.mine ? "tl-seat--mine" : ""}`}>
            <span className="tl-seat__name">{seat.name}{seat.mine ? " · vos" : ""}</span>
            <span className={`tl-seat__state ${seat.ready ? "tl-seat__state--ready" : ""}`}>
              {seat.isBot ? "Bot" : seat.ready ? "Listo" : "Confirmando fichas…"}
            </span>
          </li>
        ))}
        {free.map((i) => (
          <li key={`libre-${i}`} className="tl-seat tl-seat--free">
            <span className="muted">Asiento libre</span>
          </li>
        ))}
      </ul>

      {table.isOwner && blocker && <p id="tl-blocker" className="muted" role="status">{blocker}</p>}
      {error && <p className="notice notice--error" role="alert">{error}</p>}

      <button type="button" className="link-btn" disabled={leave.isPending} onClick={() => { setError(null); leave.mutate(table.id, { onSuccess: onExit, onError: fail }); }}>
        Salir de la mesa
      </button>

      {table.isOwner ? (
        <div className="tl-actions dock">
          <button type="button" className="btn btn--ghost" disabled={busy || table.seats.length >= table.maxPlayers} onClick={() => { setError(null); bots.add.mutate(undefined, { onError: fail }); }}>
            Agregar bot
          </button>
          <button type="button" className="btn btn--ghost" disabled={busy || botCount === 0} onClick={() => { setError(null); bots.remove.mutate(undefined, { onError: fail }); }}>
            Quitar bot
          </button>
          <button type="button" className="btn btn--gold" disabled={busy || blocker !== null} aria-describedby={blocker ? "tl-blocker" : undefined} onClick={() => { setError(null); start.mutate(table.id, { onError: fail }); }}>
            Empezar
          </button>
        </div>
      ) : (
        <p className="muted" role="status">Esperando que el dueño de la mesa empiece la partida.</p>
      )}
    </section>
    </div>
  );
}
