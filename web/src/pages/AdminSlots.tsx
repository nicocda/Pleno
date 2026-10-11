import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { ApiError } from "../api/client";
import { BACKOFFICE_ROLE, usePublishSlotsSettings, useMe, useSlotsPreview, useSlotsSettings, useSlotsSettingsHistory } from "../api/hooks";
import type { SlotsSettings, SlotsSettingsBody, SlotsSettingsView } from "../api/types";
import { useToasts } from "../components/Toasts";
import { formatChips, formatDateTime } from "../lib/format";
import { errorMessage } from "../lib/messages";
import { labelOf } from "../lib/slots";
import { useDebounced } from "../lib/useDebounced";

const MAX_SYMBOLS = 12;
const MIN_SYMBOLS = 2;
const shortId = (id: string) => `${id.slice(0, 8)}…${id.slice(-4)}`;
const percent = (value: number) => `${value.toLocaleString("es-AR", { maximumFractionDigits: 2 })} %`;

/** Lo que se edita: todo como texto, para poder escribir (y borrar) libremente antes de convertir a numeros. */
interface Draft {
  symbols: { name: string; weight: string; triple: string }[];
  leading: { symbol: string; count: string; payout: string }[];
  maxStake: string;
}

const draftFrom = (view: SlotsSettingsView): Draft => ({
  symbols: view.symbols.map((s) => ({ name: s.name, weight: String(s.weight), triple: String(s.triplePayout) })),
  leading: view.leadingPays.map((p) => ({ symbol: p.symbol, count: String(p.count), payout: String(p.payout) })),
  maxStake: String(view.maxStake),
});

const wholeNumber = (text: string): number | null => (/^\d{1,9}$/.test(text.trim()) ? Number(text) : null);

/** Convierte lo escrito a lo que entiende el servidor. Null si hay algun campo que no es un entero valido. */
export function toBody(draft: Draft, baseVersion: number): SlotsSettingsBody | null {
  const symbols = draft.symbols.map((s) => ({ name: s.name.trim(), weight: wholeNumber(s.weight), triplePayout: wholeNumber(s.triple) }));
  const leadingPays = draft.leading.map((p) => ({ symbol: p.symbol, count: wholeNumber(p.count), payout: wholeNumber(p.payout) }));
  const maxStake = wholeNumber(draft.maxStake);
  if (maxStake === null || symbols.some((s) => s.name === "" || s.weight === null || s.triplePayout === null) || leadingPays.some((p) => p.count === null || p.payout === null)) {
    return null;
  }
  return { symbols: symbols as SlotsSettingsBody["symbols"], leadingPays: leadingPays as SlotsSettingsBody["leadingPays"], maxStake, baseVersion };
}

/** Ajustes de la tragamonedas: tabla de pagos, pesos y apuesta maxima. Se prueba antes de publicar y cada cambio queda versionado. */
export function AdminSlots() {
  const me = useMe();
  const isAdmin = me.data?.roles.includes(BACKOFFICE_ROLE) ?? false;
  const settings = useSlotsSettings(isAdmin);

  if (me.isLoading) return <p className="muted" role="status">Cargando…</p>;
  if (!isAdmin) return <p className="notice notice--error" role="alert">No tenés permisos de administrador.</p>;

  return (
    <div className="stack">
      <section className="card" aria-labelledby="slots-titulo">
        <p className="eyebrow">Administración</p>
        <h1 id="slots-titulo" className="display">Ajustes de la tragamonedas</h1>
        <p className="muted">
          Cada rodillo es una cinta donde cada símbolo aparece tantas veces como su <strong>peso</strong>. El <strong>retorno</strong> es lo que la máquina
          devuelve en promedio de cada 100 fichas apostadas. <Link to="/admin">Volver al panel</Link>
        </p>
      </section>

      {settings.isError && <p className="notice notice--error" role="alert">No pudimos cargar los ajustes.</p>}
      {settings.isLoading && <p className="muted" role="status">Cargando ajustes…</p>}
      {/* La clave reinicia el borrador cuando se publica una version nueva o se recarga. */}
      {settings.data && <Editor key={settings.data.version} settings={settings.data} />}
      {isAdmin && <History />}
    </div>
  );
}

function Editor({ settings }: { settings: SlotsSettings }) {
  const toasts = useToasts();
  const publish = usePublishSlotsSettings();
  const [draft, setDraft] = useState<Draft>(() => draftFrom(settings.current));
  const [conflict, setConflict] = useState<string | null>(null);

  const body = useMemo(() => toBody(draft, settings.version), [draft, settings.version]);
  const key = body ? JSON.stringify(body) : null;
  const settledKey = useDebounced(key);
  const previewBody = useMemo(() => (settledKey ? (JSON.parse(settledKey) as SlotsSettingsBody) : null), [settledKey]);
  const preview = useSlotsPreview(previewBody);

  const original = JSON.stringify(toBody(draftFrom(settings.current), settings.version));
  const changed = key !== original;
  const typing = key !== settledKey;
  const result = preview.data;
  const canPublish = body !== null && !typing && result?.valid === true && changed && !publish.isPending;

  const symbolNames = draft.symbols.map((s) => s.name.trim()).filter(Boolean);
  const setSymbol = (i: number, patch: Partial<Draft["symbols"][number]>) =>
    setDraft((d) => ({ ...d, symbols: d.symbols.map((s, k) => (k === i ? { ...s, ...patch } : s)) }));
  const setLeading = (i: number, patch: Partial<Draft["leading"][number]>) =>
    setDraft((d) => ({ ...d, leading: d.leading.map((p, k) => (k === i ? { ...p, ...patch } : p)) }));
  const removeSymbol = (i: number) =>
    setDraft((d) => {
      const removed = d.symbols[i]!.name;
      return { ...d, symbols: d.symbols.filter((_, k) => k !== i), leading: d.leading.filter((p) => p.symbol !== removed) };
    });

  const submit = () => {
    if (!body || !canPublish) return;
    setConflict(null);
    publish.mutate(body, {
      onSuccess: (saved) => toasts.show("win", `Se publicó la versión ${saved.version}.`),
      onError: (error) => {
        if (error instanceof ApiError && error.status === 409) setConflict(error.message);
        else toasts.show("error", error instanceof ApiError && error.status === 400 ? error.message : errorMessage(error));
      },
    });
  };

  return (
    <>
      <section className="card" aria-labelledby="slots-simbolos">
        <h2 id="slots-simbolos" className="section-title">Símbolos y premios</h2>
        <p className="muted">Versión vigente: <strong>{settings.version === 0 ? "0 (la de la configuración)" : settings.version}</strong>.</p>
        <div className="admin-audit-scroll">
          <table className="admin-audit" aria-label="Símbolos">
            <thead>
              <tr><th>Símbolo</th><th className="num">Peso</th><th className="num">Premio por tres iguales (x apuesta)</th><th /></tr>
            </thead>
            <tbody>
              {draft.symbols.map((s, i) => (
                <tr key={i}>
                  <td><input aria-label={`Nombre del símbolo ${i + 1}`} value={s.name} onChange={(e) => setSymbol(i, { name: e.target.value })} maxLength={20} /></td>
                  <td className="num"><input aria-label={`Peso de ${s.name || `símbolo ${i + 1}`}`} inputMode="numeric" value={s.weight} onChange={(e) => setSymbol(i, { weight: e.target.value })} /></td>
                  <td className="num"><input aria-label={`Premio de ${s.name || `símbolo ${i + 1}`}`} inputMode="numeric" value={s.triple} onChange={(e) => setSymbol(i, { triple: e.target.value })} /></td>
                  <td>
                    <button type="button" className="btn btn--ghost" disabled={draft.symbols.length <= MIN_SYMBOLS} onClick={() => removeSymbol(i)} aria-label={`Quitar ${s.name || `símbolo ${i + 1}`}`}>
                      Quitar
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <button type="button" className="btn btn--ghost" disabled={draft.symbols.length >= MAX_SYMBOLS} onClick={() => setDraft((d) => ({ ...d, symbols: [...d.symbols, { name: "", weight: "1", triple: "1" }] }))}>
          Agregar símbolo
        </button>

        <h3 className="section-subtitle">Premios chicos por racha desde la izquierda</h3>
        <p className="muted">Por ejemplo: la cereza en el primer rodillo devuelve la apuesta (x1) y dos cerezas seguidas pagan x3.</p>
        <ul className="admin-leading">
          {draft.leading.map((p, i) => (
            <li key={i}>
              <select aria-label={`Símbolo del premio por racha ${i + 1}`} value={p.symbol} onChange={(e) => setLeading(i, { symbol: e.target.value })}>
                {!symbolNames.includes(p.symbol) && <option value={p.symbol}>{p.symbol}</option>}
                {symbolNames.map((name) => <option key={name} value={name}>{labelOf(name)}</option>)}
              </select>
              <label>
                Seguidos <input aria-label={`Cantidad de la racha ${i + 1}`} inputMode="numeric" value={p.count} onChange={(e) => setLeading(i, { count: e.target.value })} />
              </label>
              <label>
                Premio x <input aria-label={`Premio de la racha ${i + 1}`} inputMode="numeric" value={p.payout} onChange={(e) => setLeading(i, { payout: e.target.value })} />
              </label>
              <button type="button" className="btn btn--ghost" onClick={() => setDraft((d) => ({ ...d, leading: d.leading.filter((_, k) => k !== i) }))} aria-label={`Quitar la racha ${i + 1}`}>
                Quitar
              </button>
            </li>
          ))}
        </ul>
        <button type="button" className="btn btn--ghost" disabled={symbolNames.length === 0} onClick={() => setDraft((d) => ({ ...d, leading: [...d.leading, { symbol: symbolNames[0]!, count: "1", payout: "1" }] }))}>
          Agregar premio por racha
        </button>

        <label className="field">
          <span>Apuesta máxima por giro (fichas)</span>
          <input inputMode="numeric" value={draft.maxStake} onChange={(e) => setDraft((d) => ({ ...d, maxStake: e.target.value }))} />
        </label>
      </section>

      <section className="card" aria-labelledby="slots-prueba">
        <h2 id="slots-prueba" className="section-title">Cómo quedaría</h2>
        {body === null && <p className="notice notice--error" role="alert">Revisá los campos: tienen que ser números enteros (el nombre no puede quedar vacío).</p>}
        {body !== null && (typing || preview.isFetching) && !result && <p className="muted" role="status">Calculando…</p>}
        {body !== null && result && !result.valid && !typing && <p className="notice notice--error" role="alert">{result.error}</p>}
        {body !== null && result?.valid && (
          <div className={`slots-preview ${typing ? "slots-preview--stale" : ""}`} data-testid="slots-preview">
            <p>
              Retorno al jugador: <strong>{percent(result.returnToPlayerPercent ?? 0)}</strong>{" "}
              <span className="muted">(vigente: {percent(settings.current.returnToPlayerPercent)})</span>
            </p>
            <p>
              Algún premio en <strong>{percent(result.hitRatePercent ?? 0)}</strong> de los giros{" "}
              <span className="muted">(vigente: {percent(settings.current.hitRatePercent)})</span>
            </p>
            {(result.returnToPlayerPercent ?? 100) < 85 && (
              <p className="notice notice--error" role="status">Ojo: es un retorno bajo. En una tragamonedas lo habitual está entre 85 % y 98 %; el jugador va a perder mucho más seguido.</p>
            )}
            {(result.returnToPlayerPercent ?? 0) > 98 && (
              <p className="notice notice--error" role="status">Ojo: casi sin ventaja para la casa. El máximo permitido es 100 %.</p>
            )}
          </div>
        )}
        {conflict && <p className="notice notice--error" role="alert">{conflict}</p>}

        <div className="actions">
          <span className="actions__group">
            <button type="button" className="btn btn--ghost" disabled={!changed} onClick={() => { setDraft(draftFrom(settings.current)); setConflict(null); }}>
              Descartar cambios
            </button>
            <button type="button" className="btn btn--ghost" onClick={() => setDraft(draftFrom(settings.baseline))}>
              Cargar los valores de la configuración
            </button>
          </span>
          <button type="button" className="btn btn--gold" disabled={!canPublish} onClick={submit}>
            {publish.isPending ? "Publicando…" : "Publicar"}
          </button>
        </div>
        <p className="muted">
          Al publicar se crea una versión nueva que usan los giros desde ese momento (puede tardar unos segundos en llegar a todas las instancias).
          Las versiones anteriores quedan guardadas.
        </p>
      </section>
    </>
  );
}

function History() {
  const history = useSlotsSettingsHistory(true);
  return (
    <section className="card" aria-labelledby="slots-historial">
      <h2 id="slots-historial" className="section-title">Historial de versiones</h2>
      {history.isError && <p className="notice notice--error" role="alert">No pudimos cargar el historial.</p>}
      {history.data?.length === 0 && <p className="muted">Todavía no se publicó ninguna versión: rige la configuración.</p>}
      {history.data && history.data.length > 0 && (
        <div className="admin-audit-scroll">
          <table className="admin-audit" aria-label="Versiones publicadas">
            <thead>
              <tr><th>Versión</th><th>Cuándo</th><th>Administrador</th><th className="num">Retorno</th><th className="num">Premio</th><th className="num">Tope</th></tr>
            </thead>
            <tbody>
              {history.data.map((v) => (
                <tr key={v.version}>
                  <td>{v.version}</td>
                  <td>{formatDateTime(v.changedAt)}</td>
                  <td className="mono">{shortId(v.changedBy)}</td>
                  <td className="num">{percent(v.returnToPlayerPercent)}</td>
                  <td className="num">{percent(v.hitRatePercent)}</td>
                  <td className="num">{formatChips(v.maxStake)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
