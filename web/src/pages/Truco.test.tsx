import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { TableSeat, TableState, TableSummary, TrucoView } from "../api/types";
import { sha256Hex } from "../lib/crash";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Truco } from "./Truco";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const TABLE_ID = "22222222-2222-4222-8222-222222222222";
const SEED = "truco-test-seed";
const iso = (offsetMs: number) => new Date(Date.now() + offsetMs).toISOString();

const mine = (overrides: Partial<TableSummary> = {}): TableSummary => ({
  id: TABLE_ID, gameId: "truco", name: "Mesa de prueba", buyIn: 10, maxPlayers: 2, players: 2, bots: 1, status: "Playing", mine: true, isPrivate: false, createdAt: iso(-60_000), ...overrides,
});

const seat = (n: number, overrides: Partial<TableSeat> = {}): TableSeat => ({ seat: n, name: `Jugador ${n + 1}`, isBot: false, mine: false, ready: true, away: false, payout: null, ...overrides });
const seats = (overrides: Partial<Record<number, Partial<TableSeat>>> = {}): TableSeat[] => [seat(0, { mine: true, ...overrides[0] }), seat(1, { isBot: true, name: "Bot 2", ...overrides[1] })];

// Mi mano: 1 de espadas (0), 4 de espadas (3) y 12 de copas (39).
const game = (overrides: Partial<TrucoView> = {}): TrucoView => ({
  you: 0, hand: [3, 0, 39], envidoPoints: 20, opponentCards: 3, table: [], bazas: [], scores: [4, 2], target: 15, mano: 0, handNo: 3, current: 0, trucoLevel: 0, pending: null,
  actions: ["envido", "mazo", "play", "real_envido", "falta_envido", "truco"], winner: -1, events: [], ...overrides,
});

const view = (overrides: Partial<TableState<TrucoView>> = {}): TableState<TrucoView> => ({
  serverNow: new Date().toISOString(), id: TABLE_ID, gameId: "truco", name: "Mesa de prueba", status: "Playing", buyIn: 10, minPlayers: 2, maxPlayers: 2, isOwner: true, isPrivate: false,
  joinCode: null, commitment: "c".repeat(64), serverSeed: null, seats: seats(), mySeat: 0, turnSeat: 0, turnEndsAt: iso(30_000), game: game(), payouts: null, ...overrides,
});

interface Scenario {
  table: TableState<TrucoView>;
}

function apiWith(scenario: Scenario, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/truco/rules": () => ({ minPlayers: 2, maxPlayers: 2, minBuyIn: 10, maxBuyIn: 1_000 }),
    "GET /games/truco/tables": () => [mine()],
    [`GET /games/truco/tables/${TABLE_ID}`]: () => ({ ...scenario.table, serverNow: new Date().toISOString() }),
    [`POST /games/truco/tables/${TABLE_ID}/action`]: () => undefined,
    ...extra,
  });
}

const actions = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === `/games/truco/tables/${TABLE_ID}/action`);

describe("Truco", () => {
  it("opens straight on the board with the score, the dealer, my hand (best card first), the rival face-down cards and my envido", async () => {
    renderApp(<Truco />, { api: apiWith({ table: view() }) });

    expect(await screen.findByTestId("tru-score-me")).toHaveTextContent("4");
    expect(screen.getByTestId("tru-score-rival")).toHaveTextContent("2");
    expect(screen.getByTestId("tru-score")).toHaveTextContent("a 15");
    expect(screen.getByTestId("tru-score")).toHaveTextContent("Vos · es mano");
    expect(screen.getByTestId("tru-info")).toHaveTextContent("sin truco · vale 1 punto");
    expect(screen.getByTestId("tru-status")).toHaveTextContent(/Es tu turno: jugá una carta o cantá \(\d+ s\)/);
    expect(screen.getByRole("img", { name: "Bot 2 tiene 3 cartas" })).toBeInTheDocument();
    expect(screen.getByTestId("tru-envido")).toHaveTextContent("Tus puntos de envido: 20");
    const cards = within(screen.getByTestId("tru-hand")).getAllByRole("button");
    expect(cards.map((c) => c.getAttribute("aria-label"))).toEqual(["Jugar 1 de espadas", "Jugar 12 de copas", "Jugar 4 de espadas"]);
    expect(screen.queryByRole("button", { name: "Crear mesa" })).not.toBeInTheDocument();
  });

  it("plays a card with one click", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Truco />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Jugar 12 de copas" }));

    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "play", card: 39 });
  });

  it("groups the call buttons (Envido, Truco, Respuesta) apart from Al mazo, and sends the move", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Truco />, { api });

    const calls = await screen.findByRole("group", { name: "Cantos" });
    expect(within(within(calls).getByRole("group", { name: "Envido" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Envido", "Real envido", "Falta envido"]);
    expect(within(within(calls).getByRole("group", { name: "Truco" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Truco"]);
    expect(within(calls).queryByRole("group", { name: "Respuesta" })).not.toBeInTheDocument();
    expect(within(within(calls).getByRole("group", { name: "Mazo" })).getByRole("button", { name: "Al mazo" })).toBeInTheDocument();

    await userEvent.click(within(within(calls).getByRole("group", { name: "Truco" })).getByRole("button", { name: "Truco" }));
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "truco" });
  });

  it("shows the pending truco, answers it and offers to raise as 'Quiero retruco'", async () => {
    const pending = { kind: "truco" as const, caller: 1, level: 1, calls: [] };
    const api = apiWith({ table: view({ game: game({ pending, actions: ["no_quiero", "quiero", "retruco"] }) }) });
    renderApp(<Truco />, { api });

    expect(await screen.findByTestId("tru-pending")).toHaveTextContent("Bot 2 cantó truco: ¿quiero?");
    expect(screen.getByTestId("tru-status")).toHaveTextContent("Te cantaron: respondé");
    const calls = screen.getByRole("group", { name: "Cantos" });
    expect(within(within(calls).getByRole("group", { name: "Respuesta" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Quiero", "No quiero"]);
    expect(within(calls).queryByRole("button", { name: "Al mazo" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "1 de espadas" })).toBeDisabled();

    await userEvent.click(within(calls).getByRole("button", { name: "Quiero retruco" }));
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "retruco" });
  });

  it("dims the hand and hides the calls when it is not my turn, and shows the call I am waiting on", async () => {
    const pending = { kind: "truco" as const, caller: 0, level: 1, calls: [] };
    renderApp(<Truco />, { api: apiWith({ table: view({ turnSeat: 1, game: game({ current: 1, actions: [], pending }) }) }) });

    expect(await screen.findByTestId("tru-status")).toHaveTextContent("Bot 2 está pensando…");
    expect(screen.getByTestId("tru-pending")).toHaveTextContent("Jugador 1 cantó truco: ¿quiero? Esperando la respuesta.");
    const hand = screen.getByTestId("tru-hand");
    for (const card of within(hand).getAllByRole("button")) {
      expect(card).toBeDisabled();
      expect(card).toHaveClass("tru-card--dim");
    }
    expect(screen.queryByRole("group", { name: "Cantos" })).not.toBeInTheDocument();
    expect(screen.getByTestId("tru-opp")).toHaveAttribute("aria-current", "true");
  });

  it("lays the played cards in ONE pile (each mano on top of the previous one), lists the result of every mano and shows the accepted truco level", async () => {
    const g = game({
      hand: [39], table: [{ seat: 0, card: 0 }, { seat: 1, card: 2 }, { seat: 1, card: 3 }, { seat: 0, card: 13 }, { seat: 0, card: 21 }], bazas: [0, -1], trucoLevel: 2, opponentCards: 0, actions: [],
    });
    renderApp(<Truco />, { api: apiWith({ table: view({ game: g }) }) });

    const pile = await screen.findByTestId("tru-pile");
    const layers = [...pile.querySelectorAll<HTMLElement>(".tru-pile__card")];
    expect(layers.map((l) => l.dataset.mano)).toEqual(["1", "1", "2", "2", "3"]); // cada carta sabe a que mano pertenece
    expect(layers.map((l) => Number(l.style.zIndex))).toEqual([1, 2, 3, 4, 5]); // y la mano nueva queda ENCIMA de la anterior
    expect(within(pile).getByRole("img", { name: "Jugaste 1 de espadas" })).toBeInTheDocument();
    expect(within(pile).getByRole("img", { name: "Bot 2 jugó 3 de espadas" })).toBeInTheDocument();
    const manos = screen.getAllByTestId("tru-mano");
    expect(manos.map((m) => m.textContent)).toEqual(["Primera mano: ganaste", "Segunda mano: parda", "Tercera mano: en juego"]);
    expect(screen.getByTestId("tru-info")).toHaveTextContent("Retruco aceptado · vale 3 puntos");
    expect(document.body.textContent).not.toMatch(/baza/i); // en el Truco se dice "mano"
  });

  it("keeps the cards of the round that just ended on the table for a few seconds, with who won it", async () => {
    const played = [{ seat: 0, card: 0 }, { seat: 1, card: 2 }, { seat: 0, card: 13 }, { seat: 1, card: 3 }];
    const scenario: Scenario = { table: view({ game: game({ handNo: 3, table: played, bazas: [0, 1], actions: [], current: 1 }) }) };
    renderApp(<Truco />, { api: apiWith(scenario) });
    expect(await within(await screen.findByTestId("tru-pile")).findAllByRole("img")).toHaveLength(4);

    // El servidor reparte la ronda siguiente de inmediato (mesa vacia): las cartas de la anterior siguen a la vista un rato.
    scenario.table = view({ game: game({ handNo: 4, table: [], bazas: [], events: [{ seat: 1, kind: "hand_end", card: null, value: 2, a: null, b: null }] }) });

    const recap = await screen.findByTestId("tru-recap", {}, { timeout: 4_000 });
    expect(recap).toHaveTextContent("Ronda 3: ganó Bot 2 +2");
    expect(within(screen.getByTestId("tru-pile")).getAllByRole("img")).toHaveLength(4);
    expect(screen.getByTestId("tru-info")).toHaveTextContent("Ronda 4");
  });

  it("announces in big signs what the rival calls and answers, but not what was already there when the page opened", async () => {
    const old = { seat: 1, kind: "truco" as const, card: null, value: null, a: null, b: null };
    const scenario: Scenario = { table: view({ game: game({ events: [old] }) }) };
    renderApp(<Truco />, { api: apiWith(scenario) });
    await screen.findByTestId("tru-score-me");
    expect(screen.queryByTestId("tru-banner")).not.toBeInTheDocument(); // lo que ya habia al entrar no se anuncia

    scenario.table = view({ game: game({ events: [old, { seat: 1, kind: "retruco", card: null, value: null, a: null, b: null }] }) });
    const sign = await screen.findByTestId("tru-banner", {}, { timeout: 2_500 });
    expect(sign).toHaveTextContent("¡RETRUCO!");
    expect(sign).toHaveTextContent("Bot 2 te cantó");

    scenario.table = view({ game: game({ events: [old, { seat: 1, kind: "retruco", card: null, value: null, a: null, b: null }, { seat: 1, kind: "hand_end", card: null, value: 3, a: null, b: null }] }) });
    await waitFor(() => expect(screen.getAllByTestId("tru-banner").map((b) => b.textContent).join(" ")).toContain("Perdiste la ronda"), { timeout: 2_500 });
  });

  it("shows the rival cards as a fan next to the table instead of a full row", async () => {
    renderApp(<Truco />, { api: apiWith({ table: view() }) });

    const fan = await screen.findByRole("img", { name: "Bot 2 tiene 3 cartas" });
    expect(fan).toHaveClass("tru-fan");
    expect(fan.querySelectorAll(".tru-card--back")).toHaveLength(3);
    expect(fan.closest(".tru-board")).not.toBeNull(); // comparte renglon con la mesa
  });

  it("says the bot is thinking while it is its turn", async () => {
    renderApp(<Truco />, { api: apiWith({ table: view({ turnSeat: 1, game: game({ current: 1, actions: [] }) }) }) });

    expect(await screen.findByTestId("tru-status")).toHaveTextContent("Bot 2 está pensando…");
  });

  it("shows the recent events in Spanish, newest first", async () => {
    const events = [
      { seat: 1, kind: "truco" as const, value: 2 },
      { seat: 0, kind: "quiero" as const },
      { seat: 0, kind: "envido_result" as const, value: 2, a: 33, b: 27 },
      { seat: 1, kind: "hand_end" as const, value: 2 },
    ].map((e) => ({ card: null, value: null, a: null, b: null, ...e }));
    renderApp(<Truco />, { api: apiWith({ table: view({ game: game({ events }) }) }) });

    const items = within(await screen.findByRole("log", { name: "Historial de la partida" })).getAllByRole("listitem");
    expect(items.map((li) => li.textContent)).toEqual(["Ronda para Bot 2: +2", "Envido: Jugador 1 tenía 33 y Bot 2 27 — ganó Jugador 1 (+2)", "Jugador 1 quiso", "Bot 2 cantó truco"]);
  });

  it("shows the server detail when a move is refused", async () => {
    const api = apiWith({ table: view() }, {
      [`POST /games/truco/tables/${TABLE_ID}/action`]: () => {
        throw new ApiError(400, "InvalidAction", "Ya no se puede cantar envido.");
      },
    });
    renderApp(<Truco />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Envido" }));

    expect(await screen.findByText("Ya no se puede cantar envido.")).toBeInTheDocument();
  });

  it("celebrates a win it saw happen, with the net amount, and offers to go back", async () => {
    const scenario: Scenario = { table: view() };
    const { hub } = renderApp(<Truco />, { api: apiWith(scenario) });
    await screen.findByRole("button", { name: "Jugar 1 de espadas" });

    scenario.table = view({ status: "Finished", turnSeat: null, turnEndsAt: null, serverSeed: SEED, payouts: [20, 0], seats: seats({ 0: { payout: 20 } }), game: game({ hand: [], actions: [], winner: 0, scores: [15, 9] }) });
    act(() => hub.emit("gameEvent", { game: "truco", kind: "tableChanged", data: JSON.stringify({ tableId: TABLE_ID, gameId: "truco", status: "Finished" }), at: iso(0) }));

    const dialog = await screen.findByRole("dialog", { name: "¡Ganaste!" });
    expect(dialog).toHaveTextContent("+10 fichas");
    expect(dialog).toHaveTextContent("Pusiste 10 y cobraste 20");
    await userEvent.click(within(dialog).getByRole("button", { name: "Continuar" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByTestId("tru-result")).toHaveTextContent("¡Ganaste! +10 fichas");
    expect(screen.getByRole("button", { name: "Volver a las mesas" })).toBeInTheDocument();
  });

  it("does not celebrate a game that was already over when the page opened", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, payouts: [20, 0], game: game({ hand: [], actions: [], winner: 0 }) });
    renderApp(<Truco />, { api: apiWith({ table: finished }) });

    expect(await screen.findByTestId("tru-result")).toHaveTextContent("¡Ganaste! +10 fichas");
    await act(() => new Promise((resolve) => setTimeout(resolve, 300)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("says who won and how much the player lost when the rival wins", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, payouts: [0, 20], game: game({ hand: [], actions: [], winner: 1, scores: [8, 15] }) });
    renderApp(<Truco />, { api: apiWith({ table: finished }) });

    const result = await screen.findByTestId("tru-result");
    expect(result).toHaveTextContent("Ganó Bot 2.");
    expect(result).toHaveTextContent("pérdida neta de 10 fichas");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("verifies in the browser that the revealed seed matches the commitment", async () => {
    const good = view({ status: "Finished", serverSeed: SEED, commitment: await sha256Hex(SEED), payouts: [0, 20], game: game({ hand: [], actions: [], winner: 1 }) });
    renderApp(<Truco />, { api: apiWith({ table: good }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla coincide con el compromiso/)).toBeInTheDocument();
  });

  it("warns when the seed does not match the commitment", async () => {
    const bad = view({ status: "Finished", serverSeed: SEED, commitment: "0".repeat(64), payouts: [0, 20], game: game({ hand: [], actions: [], winner: 1 }) });
    renderApp(<Truco />, { api: apiWith({ table: bad }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla NO coincide con el compromiso/)).toBeInTheDocument();
  });

  it("shows the commitment while playing but no verify button until the seed is revealed", async () => {
    renderApp(<Truco />, { api: apiWith({ table: view() }) });

    expect(await screen.findByText("c".repeat(64))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verificar en mi navegador" })).not.toBeInTheDocument();
  });

  it("lists the open tables of Truco when the player is not sitting anywhere", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /games/truco/rules": () => ({ minPlayers: 2, maxPlayers: 2, minBuyIn: 10, maxBuyIn: 1_000 }),
      "GET /games/truco/tables": () => [mine({ mine: false, status: "Open", players: 1, bots: 0, name: "Mesa abierta" })],
    });
    renderApp(<Truco />, { api });

    expect(await screen.findByRole("heading", { name: "Truco, quiero retruco" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Unirme a Mesa abierta" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Crear mesa" })).toBeInTheDocument();
    expect(screen.getByText(/Gana quien llega a 15 puntos y se lleva/)).toBeInTheDocument();
  });
});
