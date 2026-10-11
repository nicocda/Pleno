import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { BlackjackRound, BlackjackSeat, BlackjackTable, BlackjackTableState } from "../api/types";
import { shoeOf } from "../lib/blackjack";
import { sha256Hex } from "../lib/crash";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Blackjack } from "./Blackjack";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const SEED = "blackjack-test-seed";
const ROUND_ID = "00000000-0000-0000-0000-000000000001";
const BET_ID = "bet-1";

const iso = (offsetMs: number) => new Date(Date.now() + offsetMs).toISOString();

const tables: BlackjackTable[] = [
  { id: "mesa-1", name: "Mesa Principiantes", minStake: 1, maxStake: 100, maxSeats: 5, phase: "Betting", players: 2 },
  { id: "mesa-2", name: "Mesa Clasica", minStake: 10, maxStake: 1_000, maxSeats: 5, phase: null, players: 0 },
];

const round = (overrides: Partial<BlackjackRound> = {}): BlackjackRound => ({
  id: ROUND_ID, tableId: "mesa-1", phase: "Betting", commitment: "c".repeat(64), openedAt: iso(-1_000), bettingEndsAt: iso(6_000), finishedAt: null,
  seatCount: 0, dealer: { cards: [], hiddenCards: 0 }, activeSeat: null, turnEndsAt: null, serverSeed: null, ...overrides,
});

const seat = (overrides: Partial<BlackjackSeat> = {}): BlackjackSeat => ({
  seat: 1, stake: 50, cards: [12, 5], hand: "Playing", result: null, payout: null, status: "Resolved", mine: true, betId: BET_ID, ...overrides,
});

const state = (current: BlackjackRound | null, seats: BlackjackSeat[] = []): BlackjackTableState => ({
  serverNow: new Date().toISOString(), table: tables[0]!, bettingSeconds: 10, turnSeconds: 15, round: current, seats,
});

interface Scenario {
  current: BlackjackTableState;
}

function apiWith(scenario: Scenario, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/blackjack/tables": () => tables,
    "GET /games/blackjack/tables/mesa-1": () => ({ ...scenario.current, serverNow: new Date().toISOString() }),
    "POST /games/blackjack/tables/mesa-1/bets": () => ({ betId: BET_ID, roundId: ROUND_ID, alreadyPlaced: false }),
    ...extra,
  });
}

const placeCalls = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === "/games/blackjack/tables/mesa-1/bets");
const tableCalls = (api: FakeApi) => api.calls.filter((c) => c.path === "/games/blackjack/tables/mesa-1");

async function sit(api: FakeApi) {
  renderApp(<Blackjack />, { api });
  await userEvent.click(await screen.findByRole("button", { name: /Mesa Principiantes/ }));
}

describe("Blackjack", () => {
  it("lists the tables with their stake range, seats and state", async () => {
    renderApp(<Blackjack />, { api: apiWith({ current: state(null) }) });

    const principiantes = await screen.findByRole("button", { name: /Mesa Principiantes/ });
    expect(principiantes).toHaveTextContent("Apuesta de 1 a 100 fichas");
    expect(principiantes).toHaveTextContent("2/5 jugadores");
    expect(principiantes).toHaveTextContent("Apuestas abiertas");
    expect(screen.getByRole("button", { name: /Mesa Clasica/ })).toHaveTextContent("Esperando jugadores");
    expect(screen.getByText(/planta en todos los 17/)).toBeInTheDocument();
  });

  it("waits for the first bet when the hand has no clock yet, and places the bet with an idempotency key", async () => {
    const api = apiWith({ current: state(round({ bettingEndsAt: null })) });
    await sit(api);

    expect(await screen.findByText("Esperando la primera apuesta", { selector: "p" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: "50" }));
    await userEvent.click(screen.getByRole("button", { name: "Sentarme y apostar" }));

    await waitFor(() => expect(placeCalls(api)).toHaveLength(1));
    expect(placeCalls(api)[0]!.body).toEqual({ stake: 50 });
    expect(placeCalls(api)[0]!.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("places the bet by itself when the repeat option is on, only once per hand", async () => {
    let attempt = 0;
    const scenario: Scenario = { current: state(round({ bettingEndsAt: null })) };
    const api = apiWith(scenario, {
      "POST /games/blackjack/tables/mesa-1/bets": () => {
        attempt += 1;
        if (attempt === 2) throw new ApiError(409, "BettingClosed", "x");
        return { betId: BET_ID, roundId: ROUND_ID, alreadyPlaced: false };
      },
    });
    await sit(api);

    await userEvent.click(await screen.findByRole("radio", { name: "50" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Repetir la apuesta en cada mano" }));

    await waitFor(() => expect(placeCalls(api)).toHaveLength(1)); // se activa y apuesta sola
    expect(placeCalls(api)[0]!.body).toEqual({ stake: 50 });
  });

  it("shows the betting countdown and refuses an amount out of range or above the balance", async () => {
    await sit(apiWith({ current: state(round()) }));

    expect(await screen.findByText(/Apuestas abiertas: cierran en \d+ s/)).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText(/Otro monto/));
    await userEvent.type(screen.getByLabelText(/Otro monto/), "500");
    expect(await screen.findByText(/la apuesta va de 1 a 100 fichas/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sentarme y apostar" })).toBeDisabled();
  });

  it("reuses the idempotency key after a network failure and uses a fresh one after a 409", async () => {
    let attempt = 0;
    const api = apiWith({ current: state(round()) }, {
      "POST /games/blackjack/tables/mesa-1/bets": () => {
        attempt += 1;
        if (attempt === 1) throw new ApiError(0, "NetworkError", "sin red");
        if (attempt === 3) throw new ApiError(409, "BettingClosed", "x");
        return { betId: BET_ID, roundId: ROUND_ID, alreadyPlaced: false };
      },
    });
    await sit(api);

    await userEvent.click(await screen.findByRole("button", { name: "Sentarme y apostar" }));
    expect(await screen.findByText(/No hay conexión con el servidor/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Sentarme y apostar" }));
    await waitFor(() => expect(placeCalls(api)).toHaveLength(2));
    expect(placeCalls(api)[1]!.headers?.["Idempotency-Key"]).toBe(placeCalls(api)[0]!.headers?.["Idempotency-Key"]);

    await userEvent.click(screen.getByRole("button", { name: "Sentarme y apostar" }));
    expect(await screen.findByText(/La mesa está repartiendo o está llena/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Sentarme y apostar" }));
    await waitFor(() => expect(placeCalls(api)).toHaveLength(4));
    expect(placeCalls(api)[3]!.headers?.["Idempotency-Key"]).not.toBe(placeCalls(api)[2]!.headers?.["Idempotency-Key"]);
  });

  it("explains the 409 of a player who is already seated", async () => {
    const api = apiWith({ current: state(round()) }, {
      "POST /games/blackjack/tables/mesa-1/bets": () => {
        throw new ApiError(409, "AlreadySeated", "x");
      },
    });
    await sit(api);

    await userEvent.click(await screen.findByRole("button", { name: "Sentarme y apostar" }));

    expect(await screen.findByText("Ya tenés un asiento en esta mano.")).toBeInTheDocument();
  });

  it("hides the dealer hole card and shows the visible one with its total, and only offers Pedir and Plantarme on my turn", async () => {
    const playing = round({ phase: "Playing", seatCount: 2, dealer: { cards: [0], hiddenCards: 1 }, activeSeat: 2, turnEndsAt: iso(10_000) });
    const seats = [seat({ seat: 1, mine: false, betId: null, cards: [9, 3] }), seat({ seat: 2, cards: [12, 5] })];
    const scenario: Scenario = { current: state(playing, seats) };
    const api = apiWith(scenario, {
      "POST /games/blackjack/bets/bet-1/hit": () => ({ betId: BET_ID }),
      "POST /games/blackjack/bets/bet-1/stand": () => ({ betId: BET_ID }),
    });
    await sit(api);

    expect(await screen.findByRole("img", { name: "Carta tapada" })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "As de picas" })).toBeInTheDocument();
    expect(screen.getByText(/Es tu turno: pedí o plantate \(\d+ s\)/)).toBeInTheDocument();
    const mineSeat = screen.getByTestId("bj-seat-mine");
    expect(mineSeat).toHaveTextContent("Asiento 2 · vos");
    expect(within(mineSeat).getByRole("img", { name: "K de picas" })).toBeInTheDocument();
    expect(mineSeat).toHaveTextContent("16 · 50 fichas");

    await userEvent.click(screen.getByRole("button", { name: "Pedir" }));
    await waitFor(() => expect(api.calls.some((c) => c.path === "/games/blackjack/bets/bet-1/hit")).toBe(true));
    await userEvent.click(screen.getByRole("button", { name: "Plantarme" }));
    await waitFor(() => expect(api.calls.some((c) => c.path === "/games/blackjack/bets/bet-1/stand")).toBe(true));
  });

  it("has no Pedir or Plantarme when the turn belongs to another seat", async () => {
    const playing = round({ phase: "Playing", seatCount: 2, dealer: { cards: [0], hiddenCards: 1 }, activeSeat: 1, turnEndsAt: iso(10_000) });
    const seats = [seat({ seat: 1, mine: false, betId: null }), seat({ seat: 2 })];
    await sit(apiWith({ current: state(playing, seats) }));

    expect(await screen.findByText("Juega el asiento 1")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Pedir" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Plantarme" })).not.toBeInTheDocument();
    expect(screen.getByText(/Esperá tu turno/)).toBeInTheDocument();
  });

  it("tells the player when it is no longer their turn (409)", async () => {
    const playing = round({ phase: "Playing", seatCount: 1, dealer: { cards: [0], hiddenCards: 1 }, activeSeat: 1, turnEndsAt: iso(10_000) });
    const api = apiWith({ current: state(playing, [seat()]) }, {
      "POST /games/blackjack/bets/bet-1/hit": () => {
        throw new ApiError(409, "NotYourTurn", "x");
      },
    });
    await sit(api);

    await userEvent.click(await screen.findByRole("button", { name: "Pedir" }));

    expect(await screen.findByText("No es tu turno (o se te acabó el tiempo).")).toBeInTheDocument();
  });

  it("shows the result of each seat and the final dealer hand when the hand ends, never anybody's identity", async () => {
    const finished = round({ phase: "Finished", seatCount: 2, finishedAt: iso(-500), dealer: { cards: [0, 12], hiddenCards: 0 }, serverSeed: SEED });
    const seats = [
      seat({ seat: 1, mine: false, betId: null, cards: [9, 8], hand: "Stood", result: "Lose", payout: 0, status: "Settled" }),
      seat({ seat: 2, cards: [12, 11], hand: "Stood", result: "Push", payout: 50, status: "Settled" }),
    ];
    renderApp(<Blackjack />, { api: apiWith({ current: state(finished, seats) }) });
    await userEvent.click(await screen.findByRole("button", { name: /Mesa Principiantes/ }));

    expect(await screen.findByText("Mano terminada: la próxima empieza en unos segundos")).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "Carta tapada" })).not.toBeInTheDocument();
    expect(screen.getAllByText("Blackjack").length).toBeGreaterThanOrEqual(1); // el crupier tiene blackjack
    expect(screen.getAllByText("Empate: te devuelven la apuesta").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Perdiste: el crupier te ganó")).toBeInTheDocument();
    expect(screen.getByTestId("bj-seat-mine")).toHaveTextContent("Asiento 2 · vos");
    expect(screen.queryByText(/jugador1/)).not.toBeInTheDocument();
  });

  it("celebrates a win it saw resolve, with the net amount, but not an old one that was already there on entering", async () => {
    const playing = round({ phase: "Playing", seatCount: 1, dealer: { cards: [0], hiddenCards: 1 }, activeSeat: 1, turnEndsAt: iso(10_000) });
    const scenario: Scenario = { current: state(playing, [seat()]) };
    const { hub } = renderApp(<Blackjack />, { api: apiWith(scenario) });
    await userEvent.click(await screen.findByRole("button", { name: /Mesa Principiantes/ }));
    await screen.findByRole("button", { name: "Pedir" });

    scenario.current = state(round({ phase: "Finished", seatCount: 1, dealer: { cards: [0, 5], hiddenCards: 0 }, serverSeed: SEED }), [seat({ result: "Win", hand: "Stood", payout: 100, status: "Settled" })]);
    act(() => hub.emit("gameEvent", { game: "blackjack", kind: "tableChanged", data: JSON.stringify({ tableId: "mesa-1", roundId: ROUND_ID, phase: "Finished" }), at: new Date().toISOString() }));

    expect(await screen.findByRole("dialog", { name: "¡Ganaste!" })).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toHaveTextContent("50 fichas");
  });

  it("does not celebrate a win that was already settled when the page opened", async () => {
    const finished = round({ phase: "Finished", seatCount: 1, dealer: { cards: [0, 5], hiddenCards: 0 }, serverSeed: SEED });
    await sit(apiWith({ current: state(finished, [seat({ result: "Win", hand: "Stood", payout: 100, status: "Settled" })]) }));

    await screen.findByText("Mano terminada: la próxima empieza en unos segundos");
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("refreshes at once when the server announces a change in this table, and ignores other tables and other games", async () => {
    const scenario: Scenario = { current: state(round()) };
    const api = apiWith(scenario);
    const { hub } = renderApp(<Blackjack />, { api });
    await userEvent.click(await screen.findByRole("button", { name: /Mesa Principiantes/ }));
    await screen.findByText(/Apuestas abiertas/);
    let before = tableCalls(api).length;

    act(() => hub.emit("gameEvent", { game: "blackjack", kind: "tableChanged", data: JSON.stringify({ tableId: "mesa-2" }), at: new Date().toISOString() }));
    act(() => hub.emit("gameEvent", { game: "crash", kind: "tableChanged", data: { tableId: "mesa-1" }, at: new Date().toISOString() }));
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(tableCalls(api).length).toBe(before);

    scenario.current = state(round({ phase: "Playing", seatCount: 1, dealer: { cards: [0], hiddenCards: 1 }, activeSeat: 1, turnEndsAt: iso(10_000) }), [seat({ mine: false, betId: null })]);
    before = tableCalls(api).length;
    act(() => hub.emit("gameEvent", { game: "blackjack", kind: "tableChanged", data: JSON.stringify({ tableId: "mesa-1" }), at: new Date().toISOString() }));

    expect(await screen.findByText("Juega el asiento 1", {}, { timeout: 800 })).toBeInTheDocument();
    expect(tableCalls(api).length).toBeGreaterThan(before);
  });

  it("verifies the last hand in the browser: the commitment and the dealt cards", async () => {
    const shoe = await shoeOf(SEED, ROUND_ID);
    const finished = round({ phase: "Finished", seatCount: 1, commitment: await sha256Hex(SEED), dealer: { cards: [shoe[1]!, shoe[3]!], hiddenCards: 0 }, serverSeed: SEED });
    const seats = [seat({ seat: 1, cards: [shoe[0]!, shoe[2]!], hand: "Stood", result: "Lose", payout: 0, status: "Settled" })];
    await sit(apiWith({ current: state(finished, seats) }));

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla coincide con el compromiso/)).toBeInTheDocument();
    expect(screen.getByText(/Las cartas del reparto coinciden con el mazo recalculado/)).toBeInTheDocument();
  });

  it("warns when the dealt cards do not come from the shoe", async () => {
    const shoe = await shoeOf(SEED, ROUND_ID);
    const finished = round({ phase: "Finished", seatCount: 1, commitment: await sha256Hex(SEED), dealer: { cards: [(shoe[1]! + 1) % 52, shoe[3]!], hiddenCards: 0 }, serverSeed: SEED });
    const seats = [seat({ seat: 1, cards: [shoe[0]!, shoe[2]!], hand: "Stood", result: "Lose", payout: 0, status: "Settled" })];
    await sit(apiWith({ current: state(finished, seats) }));

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/NO coinciden con el mazo recalculado/)).toBeInTheDocument();
  });

  it("stops polling the table when the player goes back to the list", async () => {
    const api = apiWith({ current: state(round()) });
    await sit(api);
    await screen.findByText(/Apuestas abiertas/);

    await userEvent.click(screen.getByRole("button", { name: /Cambiar de mesa/ }));
    expect(await screen.findByRole("button", { name: /Mesa Clasica/ })).toBeInTheDocument();
    const before = tableCalls(api).length;
    await new Promise((resolve) => setTimeout(resolve, 1_300));

    expect(tableCalls(api).length).toBe(before);
  });
});
