import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { ApiError } from "../api/client";
import type { CrashBet, CrashRound, CrashState } from "../api/types";
import { sha256Hex } from "../lib/crash";
import { FakeHub, fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Crash } from "./Crash";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const SEED = "9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff";
const ROUND_ID = "00000000-0000-0000-0000-000000000001"; // con esta semilla y ventaja 30 explota en x3,46 (vector de referencia)

const iso = (offsetMs: number) => new Date(Date.now() + offsetMs).toISOString();

const round = (overrides: Partial<CrashRound> = {}): CrashRound => ({
  id: ROUND_ID, phase: "Betting", commitment: "c".repeat(64), openedAt: iso(-1_000), bettingEndsAt: iso(6_000), startedAt: null, crashedAt: null,
  crashPoint: null, serverSeed: null, edgePermille: 30, growthPerSecond: 0.07, ...overrides,
});

const bet = (overrides: Partial<CrashBet> = {}): CrashBet => ({
  betId: "bet-1", roundId: ROUND_ID, status: "Placed", stake: 100, autoCashOut: null, inPlay: true, cashedOutAt: null, payout: null, failureReason: null, placedAt: iso(-500), ...overrides,
});

const state = (current: CrashRound | null, myBet: CrashBet | null = null, history: CrashRound[] = []): CrashState => ({
  serverNow: new Date().toISOString(), growthPerSecond: 0.07, minStake: 1, maxStake: 10_000, round: current, myBet, history,
});

interface Scenario {
  current: CrashState;
}

function apiWith(scenario: Scenario, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/crash/state": () => ({ ...scenario.current, serverNow: new Date().toISOString() }),
    "GET /games/crash/bets": () => [],
    "POST /games/crash/bets": () => ({ betId: "bet-1", roundId: ROUND_ID }),
    ...extra,
  });
}

const placeCalls = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === "/games/crash/bets");

beforeEach(() => {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); // jsdom no dibuja: la pagina funciona igual
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("Crash", () => {
  it("waits when there is no round yet and does not let the player bet", async () => {
    renderApp(<Crash />, { api: apiWith({ current: state(null) }) });

    expect(await screen.findByText("Esperando la próxima ronda…", { selector: "p" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apostar 10 fichas/ })).toBeDisabled();
  });

  it("opens the betting window with a countdown and places the bet with an idempotency key, the stake and the automatic cash out", async () => {
    const scenario: Scenario = { current: state(round()) };
    const api = apiWith(scenario);
    renderApp(<Crash />, { api });

    expect(await screen.findByText(/Apuestas abiertas: cierran en \d s/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: "50" }));
    await userEvent.type(screen.getByLabelText(/Retiro automático/), "2,5");
    await userEvent.click(screen.getByRole("button", { name: /Apostar 50 fichas/ }));

    await waitFor(() => expect(placeCalls(api)).toHaveLength(1));
    expect(placeCalls(api)[0]!.body).toEqual({ stake: 50, autoCashOut: 2.5 });
    expect(placeCalls(api)[0]!.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("sends no automatic cash out when the field is empty", async () => {
    const api = apiWith({ current: state(round()) });
    renderApp(<Crash />, { api });

    await userEvent.click(await screen.findByRole("button", { name: /Apostar 10 fichas/ }));

    await waitFor(() => expect(placeCalls(api)).toHaveLength(1));
    expect(placeCalls(api)[0]!.body).toEqual({ stake: 10, autoCashOut: null });
  });

  it("refuses an invalid automatic cash out and an amount the player does not have", async () => {
    renderApp(<Crash />, { api: apiWith({ current: state(round()) }) });
    await screen.findByText(/Apuestas abiertas/);

    await userEvent.type(screen.getByLabelText(/Retiro automático/), "1");
    expect(await screen.findByText(/tiene que estar entre 1,01 y 100/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apostar/ })).toBeDisabled();

    await userEvent.clear(screen.getByLabelText(/Retiro automático/));
    await userEvent.click(screen.getByRole("radio", { name: "500" }));
    await userEvent.clear(screen.getByLabelText(/Otro monto/));
    await userEvent.type(screen.getByLabelText(/Otro monto/), "5000");
    expect(await screen.findByText(/No te alcanzan las fichas/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apostar/ })).toBeDisabled();
  });

  it("reuses the idempotency key when the player retries after a network failure and uses a fresh one after a definitive answer", async () => {
    let attempt = 0;
    const api = apiWith({ current: state(round()) }, {
      "POST /games/crash/bets": () => {
        attempt += 1;
        if (attempt === 1) throw new ApiError(0, "NetworkError", "sin red");
        if (attempt === 3) throw new ApiError(409, "BettingClosed", "x");
        return { betId: "bet-1", roundId: ROUND_ID };
      },
    });
    renderApp(<Crash />, { api });

    await userEvent.click(await screen.findByRole("button", { name: /Apostar 10 fichas/ }));
    expect(await screen.findByText(/No hay conexión con el servidor/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Apostar 10 fichas/ }));
    await waitFor(() => expect(placeCalls(api)).toHaveLength(2));
    expect(placeCalls(api)[1]!.headers?.["Idempotency-Key"]).toBe(placeCalls(api)[0]!.headers?.["Idempotency-Key"]); // sin red: misma clave

    await userEvent.click(screen.getByRole("button", { name: /Apostar 10 fichas/ }));
    expect(await screen.findByText(/Ya se cerró la ronda/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Apostar 10 fichas/ }));
    await waitFor(() => expect(placeCalls(api)).toHaveLength(4));
    expect(placeCalls(api)[3]!.headers?.["Idempotency-Key"]).not.toBe(placeCalls(api)[2]!.headers?.["Idempotency-Key"]); // respuesta definitiva: clave nueva
  });

  it("shows the bet as made once it is in the round and tells when the chips are being reserved or in play", async () => {
    const scenario: Scenario = { current: state(round(), bet({ inPlay: false })) };
    renderApp(<Crash />, { api: apiWith(scenario) });

    expect(await screen.findByText(/Reservando tus fichas/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apuesta hecha" })).toBeDisabled();
  });

  it("while the rocket is rising offers to cash out with the potential payout and asks the server to do it", async () => {
    const running = round({ phase: "Running", startedAt: iso(-3_000), bettingEndsAt: iso(-3_000) });
    const scenario: Scenario = { current: state(running, bet({ stake: 100 })) };
    const api = apiWith(scenario, { "POST /games/crash/bets/bet-1/cashout": () => ({ betId: "bet-1", multiplier: 234, payout: 234 }) });
    renderApp(<Crash />, { api });

    const button = await screen.findByRole("button", { name: /Retirar \d+ fichas \(\d,\d\dx\)/ });
    await userEvent.click(button);

    expect(await screen.findByText("Retiraste en 2,34x y cobrás 234 fichas.")).toBeInTheDocument();
    expect(api.calls.some((c) => c.method === "POST" && c.path === "/games/crash/bets/bet-1/cashout")).toBe(true);
  });

  it("keeps the cash out button for a bet in play: disabled while betting, enabled as soon as the clock passes the close even if the state is stale", async () => {
    const waiting = round({ bettingEndsAt: iso(60_000) });
    const { unmount } = renderApp(<Crash />, { api: apiWith({ current: state(waiting, bet()) }) });
    expect(await screen.findByRole("button", { name: /Retirar \(disponible al despegar\)/ })).toBeDisabled();
    unmount();

    const stale = round({ bettingEndsAt: iso(-500) }); // el servidor todavia dice "Betting" pero ya paso el cierre
    renderApp(<Crash />, { api: apiWith({ current: state(stale, bet()) }) });
    expect(await screen.findByRole("button", { name: /Retirar \d+ fichas/ })).toBeEnabled();
  });

  it("does not let the player bet without the live connection, and says why", async () => {
    const hub = new FakeHub();
    renderApp(<Crash />, { api: apiWith({ current: state(round()) }), hub });
    await waitFor(() => expect(screen.getByRole("button", { name: /Apostar 10 fichas/ })).toBeEnabled());

    act(() => hub.dropConnection());

    expect(await screen.findByText(/Sin conexión en vivo no se puede apostar/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apostar 10 fichas/ })).toBeDisabled();
  });

  it("explains when the rocket crashed before the cash out arrived", async () => {
    const running = round({ phase: "Running", startedAt: iso(-3_000) });
    const api = apiWith({ current: state(running, bet()) }, {
      "POST /games/crash/bets/bet-1/cashout": () => {
        throw new ApiError(409, "CrashedAlready", "x");
      },
    });
    renderApp(<Crash />, { api });

    await userEvent.click(await screen.findByRole("button", { name: /Retirar/ }));

    expect(await screen.findByText(/explotó antes de que llegara tu retiro/)).toBeInTheDocument();
  });

  it("has no cash out button when the player has no bet in play", async () => {
    const running = round({ phase: "Running", startedAt: iso(-3_000) });
    renderApp(<Crash />, { api: apiWith({ current: state(running) }) });

    await screen.findByText("El cohete está subiendo", { selector: "p" });
    expect(screen.queryByRole("button", { name: /Retirar/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apostar/ })).toBeDisabled(); // ya no se puede apostar
  });

  it("shows where it crashed and the last explosions, coloured by how high they went", async () => {
    const crashedRound = round({ phase: "Crashed", startedAt: iso(-8_000), crashedAt: iso(-1_000), crashPoint: 346, serverSeed: SEED });
    const history = [crashedRound, round({ id: "r2", phase: "Crashed", crashPoint: 120, serverSeed: SEED }), round({ id: "r3", phase: "Crashed", crashPoint: 1_500, serverSeed: SEED })];
    renderApp(<Crash />, { api: apiWith({ current: state(crashedRound, null, history) }) });

    expect(await screen.findByText("¡Explotó en 3,46x!", { selector: "p" })).toBeInTheDocument();
    const pills = screen.getByRole("list", { name: "Últimas explosiones" });
    expect(pills).toHaveTextContent("3,46x");
    expect(screen.getByText("1,20x")).toHaveClass("crash-pill--low");
    expect(screen.getByText("15,00x")).toHaveClass("crash-pill--high");
  });

  it("verifies the last round in the browser: the revealed seed matches the commitment and gives the same crash point", async () => {
    const crashedRound = round({ phase: "Crashed", commitment: await sha256Hex(SEED), startedAt: iso(-8_000), crashedAt: iso(-1_000), crashPoint: 346, serverSeed: SEED });
    renderApp(<Crash />, { api: apiWith({ current: state(crashedRound, null, [crashedRound]) }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla coincide con el compromiso/)).toBeInTheDocument();
    expect(screen.getByText(/El punto recalculado \(3,46x\) coincide/)).toBeInTheDocument();
  });

  it("warns when a round does not verify", async () => {
    const forged = round({ phase: "Crashed", commitment: await sha256Hex(SEED), startedAt: iso(-8_000), crashedAt: iso(-1_000), crashPoint: 900, serverSeed: SEED });
    renderApp(<Crash />, { api: apiWith({ current: state(forged, null, [forged]) }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/El punto recalculado es 3,46x/)).toBeInTheDocument();
  });

  it("celebrates a win it saw resolve, but not an old one that was already settled when the page opened", async () => {
    const running = round({ phase: "Running", startedAt: iso(-3_000) });
    const scenario: Scenario = { current: state(running, bet()) };
    const { hub } = renderApp(<Crash />, { api: apiWith(scenario) });
    await screen.findByRole("button", { name: /Retirar/ });

    scenario.current = state(round({ phase: "Crashed", crashPoint: 400, startedAt: iso(-8_000), crashedAt: iso(-1_000), serverSeed: SEED }), bet({ status: "Settled", inPlay: false, cashedOutAt: 250, payout: 250 }));
    act(() => hub.emit("gameEvent", { game: "crash", kind: "roundCrashed", data: {}, at: new Date().toISOString() }));

    expect(await screen.findByRole("dialog", { name: "¡Ganaste!" })).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toHaveTextContent("150 fichas"); // ganancia neta: 250 - 100
  });

  it("does not celebrate a win that was already settled when the page opened", async () => {
    const old = state(round({ phase: "Crashed", crashPoint: 400, startedAt: iso(-8_000), crashedAt: iso(-1_000), serverSeed: SEED }), bet({ status: "Settled", inPlay: false, cashedOutAt: 250, payout: 250 }));
    renderApp(<Crash />, { api: apiWith({ current: old }) });

    await screen.findByText("¡Explotó en 4,00x!", { selector: "p" });
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("refreshes at once when the server announces a phase change instead of waiting for the next poll", async () => {
    const scenario: Scenario = { current: state(round()) };
    const api = apiWith(scenario);
    const { hub } = renderApp(<Crash />, { api });
    await screen.findByText(/Apuestas abiertas/);
    const before = api.calls.filter((c) => c.path === "/games/crash/state").length;

    scenario.current = state(round({ phase: "Running", startedAt: iso(-100) }));
    act(() => hub.emit("gameEvent", { game: "crash", kind: "roundStarted", data: {}, at: new Date().toISOString() }));

    expect(await screen.findByText("El cohete está subiendo", { selector: "p" }, { timeout: 800 })).toBeInTheDocument();
    expect(api.calls.filter((c) => c.path === "/games/crash/state").length).toBeGreaterThan(before);
  });

  it("ignores live events of other games", async () => {
    const api = apiWith({ current: state(round()) });
    const { hub } = renderApp(<Crash />, { api });
    await screen.findByText(/Apuestas abiertas/);
    const before = api.calls.filter((c) => c.path === "/games/crash/state").length;

    act(() => hub.emit("gameEvent", { game: "otro-juego", kind: "x", data: {}, at: new Date().toISOString() }));
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(api.calls.filter((c) => c.path === "/games/crash/state").length).toBe(before);
  });

  it("tells a bet that arrived late, or on a round that was cut short, that the chips came back", async () => {
    const late = round({ phase: "Running", startedAt: iso(-2_000) });
    const refunded = bet({ status: "Resolved", inPlay: false, failureReason: "BettingClosed", payout: 100 });
    renderApp(<Crash />, { api: apiWith({ current: state(late, refunded) }) });

    expect(await screen.findByText(/Tu apuesta llegó tarde: te devolvimos las fichas/)).toBeInTheDocument();
  });

  it("shows the reason when the wallet rejected the bet", async () => {
    const rejected = bet({ status: "Rejected", inPlay: false, failureReason: "InsufficientFunds" });
    renderApp(<Crash />, { api: apiWith({ current: state(round(), rejected) }) });

    expect(await screen.findByText(/No te alcanzan las fichas para esa apuesta/)).toBeInTheDocument();
  });
});
