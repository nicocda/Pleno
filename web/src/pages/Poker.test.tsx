import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { PokerPlayer, PokerView, TableSeat, TableState, TableSummary } from "../api/types";
import { sha256Hex } from "../lib/crash";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Poker } from "./Poker";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const TABLE_ID = "22222222-2222-4222-8222-222222222222";
const SEED = "poker-test-seed";
const iso = (offsetMs: number) => new Date(Date.now() + offsetMs).toISOString();

const mine = (overrides: Partial<TableSummary> = {}): TableSummary => ({
  id: TABLE_ID, gameId: "poker", name: "Mesa de prueba", buyIn: 100, maxPlayers: 3, players: 3, bots: 1, status: "Playing", mine: true, isPrivate: false, createdAt: iso(-60_000), ...overrides,
});

const seat = (n: number, overrides: Partial<TableSeat> = {}): TableSeat => ({ seat: n, name: `Jugador ${n + 1}`, isBot: false, mine: false, ready: true, away: false, payout: null, ...overrides });
const seats = (overrides: Partial<Record<number, Partial<TableSeat>>> = {}): TableSeat[] => [
  seat(0, overrides[0]),
  seat(1, { mine: true, ...overrides[1] }),
  seat(2, { isBot: true, name: "Bot 3", ...overrides[2] }),
];

const player = (n: number, overrides: Partial<PokerPlayer> = {}): PokerPlayer => ({ seat: n, stack: 90, bet: 0, folded: false, allIn: false, acted: false, cards: null, cardCount: 2, ...overrides });

// Cartas: 12 = As de picas, 21 = 10 de corazones, 35 = Jota de diamantes, 0 = 2 de picas, 51 = As de treboles.
const game = (overrides: Partial<PokerView> = {}): PokerView => ({
  you: 1, hand: [12, 21], board: [35, 0, 51], street: "flop", pot: 60,
  players: [player(0, { bet: 20 }), player(1, { stack: 80, cards: [12, 21] }), player(2, { stack: 70 })],
  dealer: 0, smallBlindSeat: 1, bigBlindSeat: 2, current: 1, currentBet: 20, toCall: 20, minRaiseTo: 40, maxRaiseTo: 80,
  actions: ["call", "fold", "raise"], showdown: [], winnings: [0, 0, 0], done: false,
  events: [{ seat: 0, kind: "bet", amount: 20, allIn: false }], ...overrides,
});

const view = (overrides: Partial<TableState<PokerView>> = {}): TableState<PokerView> => ({
  serverNow: new Date().toISOString(), id: TABLE_ID, gameId: "poker", name: "Mesa de prueba", status: "Playing", buyIn: 100, minPlayers: 2, maxPlayers: 3, isOwner: true, isPrivate: false,
  joinCode: null, commitment: "c".repeat(64), serverSeed: null, seats: seats(), mySeat: 1, turnSeat: 1, turnEndsAt: iso(20_000), game: game(), payouts: null, ...overrides,
});

function apiWith(scenario: { table: TableState<PokerView> }, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/poker/rules": () => ({ minPlayers: 2, maxPlayers: 6, minBuyIn: 20, maxBuyIn: 2_000 }),
    "GET /games/poker/tables": () => [mine()],
    [`GET /games/poker/tables/${TABLE_ID}`]: () => ({ ...scenario.table, serverNow: new Date().toISOString() }),
    [`POST /games/poker/tables/${TABLE_ID}/action`]: () => undefined,
    ...extra,
  });
}

const actions = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === `/games/poker/tables/${TABLE_ID}/action`);

// Mano terminada con showdown: gana el asiento 0 con Doble pareja; yo (asiento 1) perdi mi entrada.
const finishedView = (overrides: Partial<TableState<PokerView>> = {}, g: Partial<PokerView> = {}): TableState<PokerView> =>
  view({
    status: "Finished", turnSeat: null, turnEndsAt: null, serverSeed: SEED, payouts: [200, 0, 100], seats: seats({ 0: { payout: 200 }, 1: { payout: 0 } }),
    game: game({
      done: true, street: "done", current: 0, actions: [], minRaiseTo: 0, maxRaiseTo: 0, toCall: 0, pot: 200, board: [35, 0, 51, 22, 9],
      players: [player(0, { stack: 0, cards: [10, 36] }), player(1, { stack: 0, cards: [12, 21] }), player(2, { stack: 100, folded: true })],
      showdown: [{ seat: 0, cards: [10, 36], category: "TwoPair", won: true }, { seat: 1, cards: [12, 21], category: "Pair", won: false }],
      winnings: [200, 0, 0], events: [{ seat: 0, kind: "win_showdown", amount: 200, allIn: false }], ...g,
    }),
    ...overrides,
  });

describe("Poker", () => {
  it("opens straight on the table: my two cards, the board with gaps, the pot and each player", async () => {
    renderApp(<Poker />, { api: apiWith({ table: view({ game: game({ board: [35, 0, 51, 22] }) }) }) });

    const hand = await screen.findByTestId("pk-hand");
    expect(within(hand).getByRole("img", { name: "As de picas" })).toBeInTheDocument();
    expect(within(hand).getByRole("img", { name: "10 de corazones" })).toBeInTheDocument();
    const board = screen.getByRole("group", { name: "Cartas comunitarias" });
    expect(within(board).getAllByRole("img")).toHaveLength(4);
    expect(board.querySelectorAll(".pk-card--empty")).toHaveLength(1);
    expect(screen.getByTestId("pk-pot")).toHaveTextContent("Pozo: 60");
    expect(screen.getByTestId("pk-status")).toHaveTextContent(/Es tu turno \(\d+ s\): igualá 20 o subí/);

    const players = screen.getAllByTestId("pk-player");
    expect(players).toHaveLength(3);
    expect(players[0]).toHaveTextContent("Jugador 1");
    expect(within(players[0]!).getByLabelText("Botón del repartidor")).toBeInTheDocument();
    expect(players[0]).toHaveTextContent("20"); // lo apostado en la ronda
    expect(within(players[0]!).getAllByRole("img", { name: "Carta boca abajo de Jugador 1" })).toHaveLength(2);
    expect(players[1]).toHaveTextContent("(vos)");
    expect(within(players[1]!).getByLabelText("Ciega chica")).toBeInTheDocument();
    expect(players[2]).toHaveTextContent("Bot 3");
    expect(within(players[2]!).getByLabelText("Ciega grande")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Crear mesa" })).not.toBeInTheDocument();
  });

  it("marks folded and all-in players and whose turn it is, with no actions when it is not mine", async () => {
    const table = view({
      turnSeat: 2,
      game: game({ current: 2, actions: [], toCall: 0, minRaiseTo: 0, maxRaiseTo: 0, players: [player(0, { folded: true }), player(1, { stack: 80, cards: [12, 21] }), player(2, { stack: 0, allIn: true, bet: 70 })] }),
    });
    renderApp(<Poker />, { api: apiWith({ table }) });

    expect(await screen.findByTestId("pk-status")).toHaveTextContent("Juega Bot 3");
    const players = screen.getAllByTestId("pk-player");
    expect(players[0]).toHaveTextContent("Retirado");
    expect(players[2]).toHaveTextContent("All-in");
    expect(players[2]).toHaveAttribute("aria-current", "true");
    expect(screen.queryByTestId("pk-actions")).not.toBeInTheDocument();
  });

  it("shows the turn countdown from the server clock", async () => {
    renderApp(<Poker />, { api: apiWith({ table: view({ turnSeat: 2, game: game({ current: 2, actions: [] }) }) }) });

    await screen.findByTestId("pk-status");
    expect(screen.getAllByTestId("pk-player")[2]).toHaveTextContent(/Turno \((19|20) s\)/);
  });

  it("offers fold and call N, and sends them", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Poker />, { api });

    expect(screen.queryByRole("button", { name: "Pasar" })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: "Igualar 20" }));
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "call" });

    await userEvent.click(screen.getByRole("button", { name: "Retirarme" }));
    await waitFor(() => expect(actions(api)).toHaveLength(2));
    expect(actions(api)[1]!.body).toEqual({ type: "fold" });
  });

  it("offers check instead of call when it is free", async () => {
    const api = apiWith({ table: view({ game: game({ toCall: 0, currentBet: 0, actions: ["check", "fold", "raise"], minRaiseTo: 2, maxRaiseTo: 80 }) }) });
    renderApp(<Poker />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Pasar" }));
    expect(screen.queryByRole("button", { name: /Igualar/ })).not.toBeInTheDocument();
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "check" });
  });

  it("starts the raise at the minimum, within the limits, and the half pot shortcut fills the field", async () => {
    renderApp(<Poker />, { api: apiWith({ table: view() }) });

    const field = await screen.findByRole("spinbutton", { name: "Subir a" });
    expect(field).toHaveValue(40);
    const slider = screen.getByRole("slider", { name: "Monto de la subida" });
    expect(slider).toHaveAttribute("min", "40");
    expect(slider).toHaveAttribute("max", "80");

    await userEvent.click(screen.getByRole("button", { name: "½ pozo" })); // 0 + 20 (igualar) + (60 + 20) / 2
    expect(field).toHaveValue(60);
    await userEvent.click(screen.getByRole("button", { name: "Pozo" })); // 0 + 20 + 80 = 100, acotado al maximo
    expect(field).toHaveValue(80);
  });

  it("clamps what is typed to the limits and sends raise with the total amount", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Poker />, { api });

    const field = await screen.findByRole("spinbutton", { name: "Subir a" });
    await userEvent.clear(field);
    await userEvent.type(field, "500");
    await userEvent.click(screen.getByRole("button", { name: /^(Subir a|All-in) / }));

    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "raise", to: 80 });
  });

  it("sends a raise to an amount in between", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Poker />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "½ pozo" }));
    await userEvent.click(screen.getByRole("button", { name: "Subir a 60" }));

    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "raise", to: 60 });
  });

  it("goes all-in with the All-in shortcut", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Poker />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "All-in" }));
    expect(screen.getByRole("spinbutton", { name: "Subir a" })).toHaveValue(80);
    await userEvent.click(screen.getByRole("button", { name: /^All-in 80/ }));
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "raise", to: 80 });
  });

  it("disables the raise controls when raise is not a legal move", async () => {
    renderApp(<Poker />, { api: apiWith({ table: view({ game: game({ actions: ["call", "fold"], minRaiseTo: 0, maxRaiseTo: 0 }) }) }) });

    await screen.findByRole("button", { name: "Igualar 20" });
    expect(screen.getByRole("spinbutton", { name: "Subir a" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "All-in" })).toBeDisabled();
  });

  it("shows the recent events in Spanish, newest first", async () => {
    const events = [
      { seat: 1, kind: "blind" as const, amount: 1, allIn: false },
      { seat: 0, kind: "raise" as const, amount: 40, allIn: false },
      { seat: 2, kind: "call" as const, amount: 40, allIn: false },
      { seat: 1, kind: "fold" as const, amount: null, allIn: false },
      { seat: -1, kind: "flop" as const, amount: null, allIn: false },
    ];
    renderApp(<Poker />, { api: apiWith({ table: view({ game: game({ events }) }) }) });

    const items = within(await screen.findByRole("log", { name: "Historial de la mano" })).getAllByRole("listitem");
    expect(items.map((li) => li.textContent)).toEqual([
      "Flop: Jota de diamantes, 2 de picas, As de tréboles", "Se retiró Jugador 2", "Bot 3 igualó 40", "Jugador 1 subió a 40", "Jugador 2 puso la ciega de 1",
    ]);
  });

  it("shows the server detail when a move is refused", async () => {
    const api = apiWith({ table: view() }, {
      [`POST /games/poker/tables/${TABLE_ID}/action`]: () => {
        throw new ApiError(400, "bad_raise", "Podes subir a entre 40 y 80 fichas.");
      },
    });
    renderApp(<Poker />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Igualar 20" }));

    expect(await screen.findByText("Podes subir a entre 40 y 80 fichas.")).toBeInTheDocument();
  });

  it("celebrates a win it saw happen, with the net amount (payout minus the buy-in)", async () => {
    const scenario = { table: view() };
    const { hub } = renderApp(<Poker />, { api: apiWith(scenario) });
    await screen.findByRole("button", { name: "Igualar 20" });

    scenario.table = finishedView({ payouts: [0, 250, 50], seats: seats({ 1: { payout: 250 } }) }, {
      winnings: [0, 200, 0],
      showdown: [{ seat: 0, cards: [10, 36], category: "Pair", won: false }, { seat: 1, cards: [12, 21], category: "FullHouse", won: true }],
    });
    act(() => hub.emit("gameEvent", { game: "poker", kind: "tableChanged", data: JSON.stringify({ tableId: TABLE_ID, gameId: "poker", status: "Finished" }), at: iso(0) }));

    const dialog = await screen.findByRole("dialog", { name: "¡Ganaste!" });
    expect(dialog).toHaveTextContent("+150 fichas");
    expect(dialog).toHaveTextContent("Pusiste 100 y cobraste 250");
    await userEvent.click(within(dialog).getByRole("button", { name: "Continuar" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByTestId("pk-result")).toHaveTextContent("¡Ganaste! +150 fichas");
    expect(screen.getByTestId("pk-result")).toHaveTextContent("Ganó Jugador 2 con Full");
  });

  it("says who won with what hand and how much the player lost, with the revealed hands and the winner marked", async () => {
    renderApp(<Poker />, { api: apiWith({ table: finishedView() }) });

    const result = await screen.findByTestId("pk-result");
    expect(result).toHaveTextContent("Perdiste 100 fichas");
    expect(result).toHaveTextContent("Ganó Jugador 1 con Doble pareja");
    expect(screen.getByTestId("pk-status")).toHaveTextContent("Ganó Jugador 1 con Doble pareja");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    const rows = screen.getAllByTestId("pk-showdown-row");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("Jugador 1");
    expect(rows[0]).toHaveTextContent("Doble pareja");
    expect(rows[0]).toHaveTextContent("Ganadora · +200");
    expect(rows[1]).toHaveTextContent("Pareja");
    expect(rows[1]).not.toHaveTextContent("Ganadora");
    expect(screen.getByRole("button", { name: "Volver a las mesas" })).toBeInTheDocument();
  });

  it("does not celebrate a hand that was already over when the page opened", async () => {
    renderApp(<Poker />, { api: apiWith({ table: finishedView({ payouts: [0, 250, 50] }, { winnings: [0, 200, 0] }) }) });

    expect(await screen.findByTestId("pk-result")).toHaveTextContent("¡Ganaste! +150 fichas");
    await act(() => new Promise((resolve) => setTimeout(resolve, 300)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("when everyone else folds there is no showdown and the winner is named without a hand", async () => {
    const table = finishedView({ payouts: [0, 200, 0] }, { showdown: [], players: [player(0, { folded: true }), player(1, { stack: 0, cards: [12, 21] }), player(2, { folded: true })], winnings: [0, 200, 0] });
    renderApp(<Poker />, { api: apiWith({ table }) });

    const result = await screen.findByTestId("pk-result");
    expect(result).toHaveTextContent("Ganó Jugador 2.");
    expect(screen.queryByRole("heading", { name: "Showdown" })).not.toBeInTheDocument();
  });

  it("verifies in the browser that the revealed seed matches the commitment", async () => {
    renderApp(<Poker />, { api: apiWith({ table: finishedView({ commitment: await sha256Hex(SEED) }) }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla coincide con el compromiso/)).toBeInTheDocument();
  });

  it("warns when the seed does not match the commitment", async () => {
    renderApp(<Poker />, { api: apiWith({ table: finishedView({ commitment: "0".repeat(64) }) }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla NO coincide con el compromiso/)).toBeInTheDocument();
  });

  it("shows the commitment while playing but no verify button until the seed is revealed", async () => {
    renderApp(<Poker />, { api: apiWith({ table: view() }) });

    expect(await screen.findByText("c".repeat(64))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verificar en mi navegador" })).not.toBeInTheDocument();
    expect(screen.getByText(/La semilla se revela cuando termina la mano/)).toBeInTheDocument();
  });

  it("lists the open tables of Poker when the player is not sitting anywhere", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /games/poker/rules": () => ({ minPlayers: 2, maxPlayers: 6, minBuyIn: 20, maxBuyIn: 2_000 }),
      "GET /games/poker/tables": () => [mine({ mine: false, status: "Open", players: 1, bots: 0, name: "Mesa abierta" })],
    });
    renderApp(<Poker />, { api });

    expect(await screen.findByRole("heading", { name: "Texas Hold'em" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Unirme a Mesa abierta" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Crear mesa" })).toBeInTheDocument();
  });
});
