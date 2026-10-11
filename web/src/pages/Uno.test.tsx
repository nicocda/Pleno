import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { TableSeat, TableState, TableSummary, UnoView } from "../api/types";
import { sha256Hex } from "../lib/crash";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Uno } from "./Uno";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const TABLE_ID = "11111111-1111-4111-8111-111111111111";
const SEED = "uno-test-seed";
const iso = (offsetMs: number) => new Date(Date.now() + offsetMs).toISOString();

const mine = (overrides: Partial<TableSummary> = {}): TableSummary => ({
  id: TABLE_ID, gameId: "uno", name: "Mesa de prueba", buyIn: 10, maxPlayers: 3, players: 3, bots: 1, status: "Playing", mine: true, isPrivate: false, createdAt: iso(-60_000), ...overrides,
});

const seat = (n: number, overrides: Partial<TableSeat> = {}): TableSeat => ({ seat: n, name: `Jugador ${n + 1}`, isBot: false, mine: false, ready: true, away: false, payout: null, ...overrides });
const seats = (overrides: Partial<Record<number, Partial<TableSeat>>> = {}): TableSeat[] => [
  seat(0, overrides[0]),
  seat(1, { mine: true, ...overrides[1] }),
  seat(2, { isBot: true, name: "Bot 3", ...overrides[2] }),
];

const game = (overrides: Partial<UnoView> = {}): UnoView => ({
  you: 1, hand: [30, 5, 104], playable: [5, 104], players: [{ seat: 0, cards: 4 }, { seat: 1, cards: 3 }, { seat: 2, cards: 6 }], top: 8, topColor: 0, drawCount: 70, current: 1,
  direction: 1, drawnCard: null, winner: -1, events: [], ...overrides,
});

const view = (overrides: Partial<TableState<UnoView>> = {}): TableState<UnoView> => ({
  serverNow: new Date().toISOString(), id: TABLE_ID, gameId: "uno", name: "Mesa de prueba", status: "Playing", buyIn: 10, minPlayers: 2, maxPlayers: 3, isOwner: true, isPrivate: false,
  joinCode: null, commitment: "c".repeat(64), serverSeed: null, seats: seats(), mySeat: 1, turnSeat: 1, turnEndsAt: iso(20_000), game: game(), payouts: null, ...overrides,
});

interface Scenario {
  table: TableState<UnoView>;
}

function apiWith(scenario: Scenario, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/uno/rules": () => ({ minPlayers: 2, maxPlayers: 4, minBuyIn: 10, maxBuyIn: 1_000 }),
    "GET /games/uno/tables": () => [mine({ status: scenario.table.status === "Playing" ? "Playing" : "Finished" })].filter((t) => t.status === "Playing"),
    [`GET /games/uno/tables/${TABLE_ID}`]: () => ({ ...scenario.table, serverNow: new Date().toISOString() }),
    [`POST /games/uno/tables/${TABLE_ID}/action`]: () => undefined,
    ...extra,
  });
}

const actions = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === `/games/uno/tables/${TABLE_ID}/action`);
const tableCalls = (api: FakeApi) => api.calls.filter((c) => c.method === "GET" && c.path === `/games/uno/tables/${TABLE_ID}`);

describe("Uno", () => {
  it("opens straight on the board when the player is already in a game, with the hand, the top card and the others", async () => {
    renderApp(<Uno />, { api: apiWith({ table: view() }) });

    expect(await screen.findByRole("img", { name: "Carta de arriba: 8 rojo" })).toBeInTheDocument();
    expect(screen.getByTestId("uno-color")).toHaveTextContent("Color en juego: rojo");
    expect(screen.getByTestId("uno-direction")).toHaveTextContent("horario");
    expect(screen.getByText("70 en el mazo")).toBeInTheDocument();
    expect(screen.getByTestId("uno-status")).toHaveTextContent(/Es tu turno: jugá una carta o robá \(\d+ s\)/);

    const opponents = screen.getAllByTestId("uno-opp");
    expect(opponents).toHaveLength(2);
    expect(opponents[0]).toHaveTextContent("Jugador 1");
    expect(opponents[0]).toHaveTextContent("4");
    expect(opponents[1]).toHaveTextContent("Bot 3");
    expect(opponents[1]).toHaveTextContent("Bot");
    expect(screen.queryByRole("button", { name: "Crear mesa" })).not.toBeInTheDocument();
  });

  it("highlights the playable cards and dims the rest, which cannot be clicked", async () => {
    renderApp(<Uno />, { api: apiWith({ table: view() }) });

    const hand = await screen.findByTestId("uno-hand");
    const playable = within(hand).getByRole("button", { name: "Jugar 5 rojo" });
    const wild = within(hand).getByRole("button", { name: "Jugar Comodín +4" });
    const blocked = within(hand).getByRole("button", { name: "5 amarillo" });
    expect(playable).toBeEnabled();
    expect(playable).toHaveClass("uno-card--playable");
    expect(wild).toBeEnabled();
    expect(blocked).toBeDisabled();
    expect(blocked).toHaveClass("uno-card--dim");
  });

  it("plays a card with one click, with no color", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Uno />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Jugar 5 rojo" }));

    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "play", card: 5 });
  });

  it("asks for a color before playing a wild card and sends it with the move", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Uno />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Jugar Comodín +4" }));
    expect(actions(api)).toHaveLength(0);
    const picker = screen.getByRole("group", { name: "Elegí un color" });
    expect(within(picker).getAllByRole("button").map((b) => b.getAttribute("aria-label"))).toEqual(["Color rojo", "Color amarillo", "Color verde", "Color azul", null]);
    await userEvent.click(within(picker).getByRole("button", { name: "Color azul" }));

    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "play", card: 104, color: 3 });
    expect(screen.queryByRole("group", { name: "Elegí un color" })).not.toBeInTheDocument();
  });

  it("lets the player cancel the color choice", async () => {
    const api = apiWith({ table: view() });
    renderApp(<Uno />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Jugar Comodín +4" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancelar" }));

    expect(screen.queryByRole("group", { name: "Elegí un color" })).not.toBeInTheDocument();
    expect(actions(api)).toHaveLength(0);
  });

  it("tells the player to tap the pile when there is nothing to play, and only then", async () => {
    const scenario: Scenario = { table: view({ game: game({ playable: [] }) }) };
    renderApp(<Uno />, { api: apiWith(scenario) });

    expect(await screen.findByTestId("uno-draw-alert")).toHaveTextContent("tocá el mazo para robar");
    expect(screen.getByRole("button", { name: "Robar carta" })).toHaveClass("uno-pile--call");

    scenario.table = view({ game: game({ playable: [55], drawnCard: 55 }) });
    await waitFor(() => expect(screen.queryByTestId("uno-draw-alert")).not.toBeInTheDocument(), { timeout: 2_500 });
    expect(screen.getByRole("button", { name: "Robar carta" })).not.toHaveClass("uno-pile--call");
  });

  it("does not call for a draw when there is something to play", async () => {
    renderApp(<Uno />, { api: apiWith({ table: view() }) });

    await screen.findByRole("button", { name: "Robar carta" });
    expect(screen.queryByTestId("uno-draw-alert")).not.toBeInTheDocument();
  });

  it("draws from the pile, and shows Pasar only after drawing a playable card", async () => {
    const scenario: Scenario = { table: view({ game: game({ playable: [] }) }) };
    const api = apiWith(scenario);
    renderApp(<Uno />, { api });

    expect(screen.queryByRole("button", { name: "Pasar" })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: "Robar carta" }));
    await waitFor(() => expect(actions(api)).toHaveLength(1));
    expect(actions(api)[0]!.body).toEqual({ type: "draw" });

    scenario.table = view({ game: game({ hand: [30, 5, 55], playable: [55], drawnCard: 55 }) });
    const pass = await screen.findByRole("button", { name: "Pasar" }, { timeout: 2_500 });
    expect(screen.getByRole("button", { name: "Robar carta" })).toBeDisabled();
    expect(screen.getByTestId("uno-status")).toHaveTextContent("Robaste 5 verde: jugala o pasá");
    await userEvent.click(pass);
    await waitFor(() => expect(actions(api)).toHaveLength(2));
    expect(actions(api)[1]!.body).toEqual({ type: "pass" });
  });

  it("cannot play or draw when it is not the player's turn, and says whose turn it is", async () => {
    renderApp(<Uno />, { api: apiWith({ table: view({ turnSeat: 2, game: game({ current: 2, playable: [] }) }) }) });

    expect(await screen.findByTestId("uno-status")).toHaveTextContent("Juega Bot 3");
    expect(screen.getByRole("button", { name: "Robar carta" })).toBeDisabled();
    expect(within(screen.getByTestId("uno-hand")).queryByRole("button", { name: /^Jugar / })).not.toBeInTheDocument();
    expect(screen.getAllByTestId("uno-opp")[1]).toHaveAttribute("aria-current", "true");
  });

  it("marks an absent player and a player who is one card away", async () => {
    const table = view({ seats: seats({ 0: { away: true } }), game: game({ current: 0, playable: [], players: [{ seat: 0, cards: 1 }, { seat: 1, cards: 3 }, { seat: 2, cards: 6 }] }) });
    renderApp(<Uno />, { api: apiWith({ table }) });

    expect(await screen.findByTestId("uno-status")).toHaveTextContent("Juega Jugador 1 (ausente: lo juega un bot)");
    expect(screen.getByTestId("uno-alert")).toHaveTextContent("Jugador 1 quedó con una carta: ¡Uno!");
    expect(screen.getAllByTestId("uno-opp")[0]).toHaveTextContent("Ausente: lo juega un bot");
  });

  it("shows the direction when it is reversed and the recent events in Spanish, newest first", async () => {
    const events = [
      { seat: 0, kind: "play" as const, card: 73, color: 2 },
      { seat: 1, kind: "skipped" as const, card: null, color: null },
      { seat: 2, kind: "draw" as const, card: null, color: null, count: 1 },
      { seat: 0, kind: "penalty" as const, card: null, color: null, count: 2 },
    ].map((e) => ({ count: null, ...e }));
    renderApp(<Uno />, { api: apiWith({ table: view({ game: game({ direction: -1, events }) }) }) });

    expect(await screen.findByTestId("uno-direction")).toHaveTextContent("antihorario");
    const items = within(screen.getByRole("log", { name: "Historial de la partida" })).getAllByRole("listitem");
    expect(items.map((li) => li.textContent)).toEqual(["Jugador 1 levantó 2 cartas", "Bot 3 robó una carta", "Jugador 2 se salteó", "Jugador 1 jugó un +2 verde"]);
  });

  it("shows the server detail when a move is refused", async () => {
    const api = apiWith({ table: view() }, {
      [`POST /games/uno/tables/${TABLE_ID}/action`]: () => {
        throw new ApiError(400, "InvalidAction", "Esa carta no se puede jugar sobre un 8 rojo.");
      },
    });
    renderApp(<Uno />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Jugar 5 rojo" }));

    expect(await screen.findByText("Esa carta no se puede jugar sobre un 8 rojo.")).toBeInTheDocument();
  });

  it("celebrates a win it saw happen, with the net amount (pot minus the buy-in), and offers to go back", async () => {
    const scenario: Scenario = { table: view() };
    const { hub } = renderApp(<Uno />, { api: apiWith(scenario) });
    await screen.findByRole("button", { name: "Robar carta" });

    scenario.table = view({ status: "Finished", turnSeat: null, turnEndsAt: null, serverSeed: SEED, payouts: [0, 30, 0], seats: seats({ 1: { payout: 30 } }), game: game({ hand: [], playable: [], winner: 1, current: 1 }) });
    act(() => hub.emit("gameEvent", { game: "uno", kind: "tableChanged", data: JSON.stringify({ tableId: TABLE_ID, gameId: "uno", status: "Finished" }), at: iso(0) }));

    const dialog = await screen.findByRole("dialog", { name: "¡Ganaste!" });
    expect(dialog).toHaveTextContent("+20 fichas");
    expect(dialog).toHaveTextContent("Pusiste 10 y cobraste 30");
    await userEvent.click(within(dialog).getByRole("button", { name: "Continuar" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByTestId("uno-result")).toHaveTextContent("¡Ganaste! +20 fichas");
    expect(screen.getByRole("button", { name: "Volver a las mesas" })).toBeInTheDocument();
  });

  it("does not celebrate a game that was already over when the page opened", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, payouts: [0, 30, 0], game: game({ hand: [], playable: [], winner: 1 }) });
    const api = apiWith({ table: finished }, { "GET /games/uno/tables": () => [mine({ status: "Playing" })] });
    renderApp(<Uno />, { api });

    expect(await screen.findByTestId("uno-result")).toHaveTextContent("¡Ganaste! +20 fichas");
    await act(() => new Promise((resolve) => setTimeout(resolve, 300)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("says who won and how much the player lost when someone else wins", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, payouts: [30, 0, 0], game: game({ hand: [5], playable: [], winner: 0, current: 0 }) });
    renderApp(<Uno />, { api: apiWith({ table: finished }, { "GET /games/uno/tables": () => [mine()] }) });

    const result = await screen.findByTestId("uno-result");
    expect(result).toHaveTextContent("Ganó Jugador 1.");
    expect(result).toHaveTextContent("pérdida neta de 10 fichas");
    expect(screen.getByTestId("uno-status")).toHaveTextContent("Ganó Jugador 1");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("verifies in the browser that the revealed seed matches the commitment", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, commitment: await sha256Hex(SEED), payouts: [30, 0, 0], game: game({ hand: [5], playable: [], winner: 0 }) });
    renderApp(<Uno />, { api: apiWith({ table: finished }, { "GET /games/uno/tables": () => [mine()] }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla coincide con el compromiso/)).toBeInTheDocument();
  });

  it("warns when the seed does not match the commitment", async () => {
    const finished = view({ status: "Finished", serverSeed: SEED, commitment: "0".repeat(64), payouts: [30, 0, 0], game: game({ hand: [5], playable: [], winner: 0 }) });
    renderApp(<Uno />, { api: apiWith({ table: finished }, { "GET /games/uno/tables": () => [mine()] }) });

    await userEvent.click(await screen.findByRole("button", { name: "Verificar en mi navegador" }));

    expect(await screen.findByText(/La semilla NO coincide con el compromiso/)).toBeInTheDocument();
  });

  it("shows the commitment while playing but no verify button until the seed is revealed", async () => {
    renderApp(<Uno />, { api: apiWith({ table: view() }) });

    expect(await screen.findByText("c".repeat(64))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verificar en mi navegador" })).not.toBeInTheDocument();
    expect(screen.getByText(/La semilla se revela cuando termina la partida/)).toBeInTheDocument();
  });

  it("refreshes the table at once on a tableChanged notice of Uno, and ignores other games", async () => {
    const api = apiWith({ table: view() });
    const { hub } = renderApp(<Uno />, { api });
    await screen.findByRole("button", { name: "Robar carta" });
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    let before = tableCalls(api).length;

    act(() => hub.emit("gameEvent", { game: "crash", kind: "tableChanged", data: "{}", at: iso(0) }));
    await act(() => new Promise((resolve) => setTimeout(resolve, 150)));
    expect(tableCalls(api).length).toBe(before);

    before = tableCalls(api).length;
    act(() => hub.emit("gameEvent", { game: "uno", kind: "tableChanged", data: JSON.stringify({ tableId: TABLE_ID, gameId: "uno", status: "Playing" }), at: iso(0) }));
    await waitFor(() => expect(tableCalls(api).length).toBeGreaterThan(before), { timeout: 800 });
  });

  it("lists the open tables of Uno when the player is not sitting anywhere", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /games/uno/rules": () => ({ minPlayers: 2, maxPlayers: 4, minBuyIn: 10, maxBuyIn: 1_000 }),
      "GET /games/uno/tables": () => [mine({ mine: false, status: "Open", players: 1, bots: 0, name: "Mesa abierta" })],
    });
    renderApp(<Uno />, { api });

    expect(await screen.findByRole("heading", { name: "Quedate sin cartas" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Unirme a Mesa abierta" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Crear mesa" })).toBeInTheDocument();
    expect(screen.getByText(/gana la suma de todas las entradas/)).toBeInTheDocument();
  });
});
