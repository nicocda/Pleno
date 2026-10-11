import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { TableSeat, TableState, TableSummary } from "../api/types";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { startBlocker, TablesLobby } from "./TablesLobby";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };
const TABLE_ID = "11111111-1111-4111-8111-111111111111";
const OTHER_ID = "22222222-2222-4222-8222-222222222222";

const summary = (overrides: Partial<TableSummary> = {}): TableSummary => ({
  id: OTHER_ID, gameId: "uno", name: "Mesa de Ana", buyIn: 50, maxPlayers: 4, players: 2, bots: 1, status: "Open", mine: false, isPrivate: false, createdAt: new Date().toISOString(), ...overrides,
});

const seat = (n: number, overrides: Partial<TableSeat> = {}): TableSeat => ({ seat: n, name: `Jugador ${n + 1}`, isBot: false, mine: false, ready: true, away: false, payout: null, ...overrides });

const view = (overrides: Partial<TableState> = {}): TableState => ({
  serverNow: new Date().toISOString(), id: TABLE_ID, gameId: "uno", name: "Mi mesa", status: "Open", buyIn: 10, minPlayers: 2, maxPlayers: 4, isOwner: true, isPrivate: false,
  joinCode: null, commitment: "c".repeat(64), serverSeed: null, seats: [seat(0, { mine: true })], mySeat: 0, turnSeat: null, turnEndsAt: null, game: null, payouts: null, ...overrides,
});

interface Scenario {
  list: TableSummary[];
  table: TableState;
}

function apiWith(scenario: Scenario, extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account,
    "GET /games/uno/rules": () => ({ minPlayers: 2, maxPlayers: 4, minBuyIn: 10, maxBuyIn: 1_000 }),
    "GET /games/uno/tables": () => scenario.list,
    [`GET /games/uno/tables/${TABLE_ID}`]: () => ({ ...scenario.table, serverNow: new Date().toISOString() }),
    [`POST /games/uno/tables/${OTHER_ID}/join`]: () => undefined,
    [`POST /games/uno/tables/${TABLE_ID}/join`]: () => undefined,
    ...extra,
  });
}

const posts = (api: FakeApi, path: string) => api.calls.filter((c) => c.method === "POST" && c.path === path);

function renderLobby(api: FakeApi) {
  return renderApp(<TablesLobby gameId="uno" gameName="Uno" renderGame={(t, leave) => (
    <div>
      <p>Jugando en {t.name} ({t.status})</p>
      <button type="button" onClick={leave}>Salir al lobby</button>
    </div>
  )} />, { api });
}

describe("TablesLobby: la lista", () => {
  it("lists the open tables with their buy-in, players and bots, and nothing else", async () => {
    renderLobby(apiWith({ list: [summary(), summary({ id: "x", name: "Ya empezada", status: "Playing" })], table: view() }));

    const item = (await screen.findByText("Mesa de Ana")).closest("li")!;
    expect(item).toHaveTextContent("Entrada de 50 fichas");
    expect(item).toHaveTextContent("2/4 jugadores (1 bot)");
    expect(screen.queryByText("Ya empezada")).not.toBeInTheDocument();
  });

  it("guides the player with the three steps and explains the create form fields", async () => {
    renderLobby(apiWith({ list: [], table: view() }));

    const steps = await screen.findByRole("list", { name: "Cómo se juega en mesa" });
    expect(within(steps).getAllByRole("listitem")).toHaveLength(3);
    expect(within(steps).getByText(/Creá una mesa o unite/).closest("li")).toHaveAttribute("aria-current", "step");
    expect(await screen.findByText(/El ganador se lleva todo el pozo: con 2 jugadores, 20 fichas/)).toBeInTheDocument();
    expect(screen.getByText(/Aparece en la lista de mesas abiertas/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("checkbox", { name: /Mesa privada/ }));
    expect(screen.getByText(/te damos un código de 6 caracteres/)).toBeInTheDocument();
  });

  it("says so when there are no open tables", async () => {
    renderLobby(apiWith({ list: [], table: view() }));
    expect(await screen.findByText(/No hay mesas abiertas/)).toBeInTheDocument();
  });

  it("sits the player with one click and takes them to the waiting room", async () => {
    const scenario: Scenario = { list: [summary({ id: TABLE_ID })], table: view({ name: "Mesa de Ana", isOwner: false, seats: [seat(0), seat(1, { mine: true })], mySeat: 1 }) };
    const api = apiWith(scenario);
    renderLobby(api);

    await userEvent.click(await screen.findByRole("button", { name: "Unirme a Mesa de Ana" }));

    await waitFor(() => expect(posts(api, `/games/uno/tables/${TABLE_ID}/join`)).toHaveLength(1));
    expect(posts(api, `/games/uno/tables/${TABLE_ID}/join`)[0]!.body).toBeUndefined();
    expect(await screen.findByRole("heading", { name: "Mesa de Ana" })).toBeInTheDocument();
    expect(screen.getByText(/Esperando que el dueño de la mesa empiece/)).toBeInTheDocument();
  });

  it("shows the server detail when sitting fails", async () => {
    const api = apiWith({ list: [summary()], table: view() }, {
      [`POST /games/uno/tables/${OTHER_ID}/join`]: () => {
        throw new ApiError(409, "AlreadySeated", "Ya estás sentado en otra mesa de Uno.");
      },
    });
    renderLobby(api);

    await userEvent.click(await screen.findByRole("button", { name: "Unirme a Mesa de Ana" }));

    expect(await screen.findByText("Ya estás sentado en otra mesa de Uno.")).toBeInTheDocument();
  });

  it("disables a full table", async () => {
    renderLobby(apiWith({ list: [summary({ players: 4 })], table: view() }));
    expect(await screen.findByRole("button", { name: "Unirme a Mesa de Ana" })).toBeDisabled();
  });

  it("takes the player straight to their own table, with no way to create another", async () => {
    renderLobby(apiWith({ list: [summary({ id: TABLE_ID, mine: true, name: "Mi mesa" })], table: view() }));

    expect(await screen.findByRole("heading", { name: "Mi mesa" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Crear mesa" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Salir de la mesa" })).toBeInTheDocument();
  });
});

describe("TablesLobby: crear mesa", () => {
  it("creates a table with the form values and an idempotency key", async () => {
    const api = apiWith({ list: [], table: view({ name: "Los pibes" }) }, { "POST /games/uno/tables": () => ({ tableId: TABLE_ID, joinCode: null, alreadyCreated: false }) });
    renderLobby(api);

    await userEvent.type(await screen.findByLabelText("Nombre (opcional)"), "Los pibes");
    await userEvent.clear(screen.getByLabelText("Entrada (fichas)"));
    await userEvent.type(screen.getByLabelText("Entrada (fichas)"), "25");
    await userEvent.click(screen.getByRole("radio", { name: "3" }));
    await userEvent.click(screen.getByRole("button", { name: "Crear mesa" }));

    await waitFor(() => expect(posts(api, "/games/uno/tables")).toHaveLength(1));
    const call = posts(api, "/games/uno/tables")[0]!;
    expect(call.body).toEqual({ buyIn: 25, maxPlayers: 3, isPrivate: false, name: "Los pibes" });
    expect(call.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(await screen.findByRole("heading", { name: "Los pibes" })).toBeInTheDocument();
  });

  it("offers the players between minPlayers and maxPlayers and refuses a buy-in out of range", async () => {
    renderLobby(apiWith({ list: [], table: view() }));

    const group = await screen.findByRole("radiogroup", { name: "Jugadores" });
    expect(within(group).getAllByRole("radio").map((r) => r.textContent)).toEqual(["2", "3", "4"]);
    await userEvent.clear(screen.getByLabelText("Entrada (fichas)"));
    await userEvent.type(screen.getByLabelText("Entrada (fichas)"), "5");

    expect(await screen.findByText(/La entrada va de 10 a 1\.000 fichas|La entrada va de 10 a 1000 fichas/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Crear mesa" })).toBeDisabled();
  });

  it("refuses a buy-in above the balance", async () => {
    renderLobby(apiWith({ list: [], table: view() }, { "GET /wallet/me": () => ({ ...account, available: 500 }) }));

    await userEvent.clear(await screen.findByLabelText("Entrada (fichas)"));
    await userEvent.type(screen.getByLabelText("Entrada (fichas)"), "1000");
    await screen.findByText(/No te alcanzan las fichas para esa entrada/);
    expect(screen.getByRole("button", { name: "Crear mesa" })).toBeDisabled();
  });

  it("reuses the key after a network failure and uses a new one after a definitive error", async () => {
    let attempt = 0;
    const api = apiWith({ list: [], table: view() }, {
      "POST /games/uno/tables": () => {
        attempt += 1;
        if (attempt === 1) throw new ApiError(0, "NetworkError", "sin red");
        if (attempt === 2) throw new ApiError(409, "AlreadySeated", "Ya estás sentado en otra mesa de Uno.");
        return { tableId: TABLE_ID, joinCode: null, alreadyCreated: false };
      },
    });
    renderLobby(api);

    await userEvent.click(await screen.findByRole("button", { name: "Crear mesa" }));
    expect(await screen.findByText(/No hay conexión con el servidor/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Crear mesa" }));
    expect(await screen.findByText("Ya estás sentado en otra mesa de Uno.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Crear mesa" }));

    await waitFor(() => expect(posts(api, "/games/uno/tables")).toHaveLength(3));
    const keys = posts(api, "/games/uno/tables").map((c) => c.headers?.["Idempotency-Key"]);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[1]);
  });

  it("creates a private table, shows its code and copies it", async () => {
    const user = userEvent.setup();
    const scenario: Scenario = { list: [], table: view({ isPrivate: true, joinCode: "K7M2QX" }) };
    const api = apiWith(scenario, { "POST /games/uno/tables": () => ({ tableId: TABLE_ID, joinCode: "K7M2QX", alreadyCreated: false }) });
    renderLobby(api);

    await user.click(await screen.findByRole("checkbox", { name: /Mesa privada/ }));
    await user.click(screen.getByRole("button", { name: "Crear mesa" }));

    expect(await screen.findByTestId("tl-code")).toHaveTextContent("K7M2QX");
    expect(posts(api, "/games/uno/tables")[0]!.body).toMatchObject({ isPrivate: true });
    await user.click(screen.getByRole("button", { name: "Copiar código" }));
    expect(await screen.findByRole("button", { name: "¡Copiado!" })).toBeInTheDocument();
    expect(await navigator.clipboard.readText()).toBe("K7M2QX");
  });
});

describe("TablesLobby: unirse con un código", () => {
  it("joins a private table with only the code and goes to the table the server answers", async () => {
    const scenario: Scenario = { list: [], table: view({ isOwner: false, isPrivate: true, seats: [seat(0), seat(1, { mine: true })], mySeat: 1 }) };
    const api = apiWith(scenario, { "POST /games/uno/tables/join": () => ({ tableId: TABLE_ID }) });
    renderLobby(api);

    await userEvent.type(await screen.findByLabelText("Tengo un código"), "k7m2qx");
    await userEvent.click(screen.getByRole("button", { name: "Entrar con el código" }));

    await waitFor(() => expect(posts(api, "/games/uno/tables/join")).toHaveLength(1));
    expect(posts(api, "/games/uno/tables/join")[0]!.body).toEqual({ code: "k7m2qx" });
    expect(await screen.findByRole("heading", { name: "Mi mesa" })).toBeInTheDocument();
  });

  it("explains a wrong code with the server message and rejects a code of the wrong length without calling", async () => {
    const api = apiWith({ list: [], table: view() }, {
      "POST /games/uno/tables/join": () => {
        throw new ApiError(400, "InvalidAction", "El codigo de la mesa no es correcto.");
      },
    });
    renderLobby(api);

    await userEvent.type(await screen.findByLabelText("Tengo un código"), "abc");
    await userEvent.click(screen.getByRole("button", { name: "Entrar con el código" }));
    expect(await screen.findByText("El código tiene 6 caracteres.")).toBeInTheDocument();
    expect(posts(api, "/games/uno/tables/join")).toHaveLength(0);

    await userEvent.type(screen.getByLabelText("Tengo un código"), "def");
    await userEvent.click(screen.getByRole("button", { name: "Entrar con el código" }));
    expect(await screen.findByText("El codigo de la mesa no es correcto.")).toBeInTheDocument();
  });
});

describe("TablesLobby: la sala de espera", () => {
  const room = (overrides: Partial<TableState> = {}): Scenario => ({ list: [summary({ id: TABLE_ID, mine: true, name: "Mi mesa" })], table: view(overrides) });

  it("shows the seats with their state and blocks Empezar with the reason when seats are missing", async () => {
    renderLobby(apiWith(room()));

    const seats = await screen.findByRole("list", { name: "Asientos" });
    expect(within(seats).getByText("Jugador 1 · vos")).toBeInTheDocument();
    expect(within(seats).getAllByText("Asiento libre")).toHaveLength(3);
    expect(screen.getByRole("button", { name: "Empezar" })).toBeDisabled();
    expect(screen.getByText(/Faltan 1 jugador para empezar \(mínimo 2\)/)).toBeInTheDocument();
  });

  it("pins the main buttons at the bottom (dock) in the create form and in the room", async () => {
    const { unmount } = renderLobby(apiWith({ list: [], table: view() }));
    expect((await screen.findByRole("button", { name: "Crear mesa" })).closest(".dock")).not.toBeNull();
    unmount();

    renderLobby(apiWith(room()));
    expect((await screen.findByRole("button", { name: "Empezar" })).closest(".dock")).not.toBeNull();
    expect(screen.getByRole("button", { name: "Agregar bot" }).closest(".dock")).not.toBeNull();
  });

  it("shows the room as step 2 while the table is not ready to start", async () => {
    renderLobby(apiWith(room()));

    await screen.findByRole("list", { name: "Asientos" });
    const steps = screen.getByRole("list", { name: "Cómo se juega en mesa" });
    expect(within(steps).getByText(/Sumá bots o esperá/).closest("li")).toHaveAttribute("aria-current", "step");
  });

  it("blocks Empezar while the chips of a seat are not confirmed, then allows it", async () => {
    const scenario = room({ seats: [seat(0, { mine: true }), seat(1, { ready: false })] });
    const api = apiWith(scenario, { [`POST /games/uno/tables/${TABLE_ID}/start`]: () => undefined });
    renderLobby(api);

    expect(await screen.findByText("Confirmando fichas…")).toBeInTheDocument();
    expect(screen.getByText(/Esperando que se confirmen las fichas de 1 asiento/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Empezar" })).toBeDisabled();

    scenario.table = view({ seats: [seat(0, { mine: true }), seat(1)] });
    await waitFor(() => expect(screen.getByRole("button", { name: "Empezar" })).toBeEnabled(), { timeout: 2_500 });
    await userEvent.click(screen.getByRole("button", { name: "Empezar" }));
    await waitFor(() => expect(posts(api, `/games/uno/tables/${TABLE_ID}/start`)).toHaveLength(1));
  });

  it("adds and removes bots and enables Quitar bot only when there is one", async () => {
    const scenario = room();
    const api = apiWith(scenario, {
      [`POST /games/uno/tables/${TABLE_ID}/bots`]: () => undefined,
      [`DELETE /games/uno/tables/${TABLE_ID}/bots`]: () => undefined,
    });
    renderLobby(api);

    expect(await screen.findByRole("button", { name: "Quitar bot" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Agregar bot" }));
    await waitFor(() => expect(posts(api, `/games/uno/tables/${TABLE_ID}/bots`)).toHaveLength(1));

    scenario.table = view({ seats: [seat(0, { mine: true }), seat(1, { isBot: true, name: "Bot 2" })] });
    await waitFor(() => expect(screen.getByRole("button", { name: "Quitar bot" })).toBeEnabled(), { timeout: 2_500 });
    expect(screen.getByRole("button", { name: "Empezar" })).toBeEnabled();
    await userEvent.click(screen.getByRole("button", { name: "Quitar bot" }));
    await waitFor(() => expect(api.calls.some((c) => c.method === "DELETE" && c.path === `/games/uno/tables/${TABLE_ID}/bots`)).toBe(true));
  });

  it("shows the reason when the server refuses to start", async () => {
    const api = apiWith(room({ seats: [seat(0, { mine: true }), seat(1)] }), {
      [`POST /games/uno/tables/${TABLE_ID}/start`]: () => {
        throw new ApiError(409, "TableClosed", "Todavía hay fichas sin confirmar.");
      },
    });
    renderLobby(api);

    await userEvent.click(await screen.findByRole("button", { name: "Empezar" }));

    expect(await screen.findByText("Todavía hay fichas sin confirmar.")).toBeInTheDocument();
  });

  it("gives a guest no owner buttons and lets them leave, going back to the list", async () => {
    const scenario = room({ isOwner: false, seats: [seat(0), seat(1, { mine: true })], mySeat: 1 });
    const api = apiWith(scenario, { [`POST /games/uno/tables/${TABLE_ID}/leave`]: () => undefined });
    renderLobby(api);

    await screen.findByRole("button", { name: "Salir de la mesa" });
    expect(screen.queryByRole("button", { name: "Empezar" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Agregar bot" })).not.toBeInTheDocument();

    scenario.list = []; // el servidor ya no la lista como propia
    await userEvent.click(screen.getByRole("button", { name: "Salir de la mesa" }));

    await waitFor(() => expect(posts(api, `/games/uno/tables/${TABLE_ID}/leave`)).toHaveLength(1));
    expect(await screen.findByRole("heading", { name: "Mesas abiertas" })).toBeInTheDocument();
  });

  it("does not bounce back into a table just left even if the list still shows it for a moment", async () => {
    const scenario = room({ isOwner: false, seats: [seat(0), seat(1, { mine: true })], mySeat: 1 });
    renderLobby(apiWith(scenario, { [`POST /games/uno/tables/${TABLE_ID}/leave`]: () => undefined }));

    await userEvent.click(await screen.findByRole("button", { name: "Salir de la mesa" }));

    expect(await screen.findByRole("heading", { name: "Mesas abiertas" })).toBeInTheDocument();
    await act(() => new Promise((resolve) => setTimeout(resolve, 200)));
    expect(screen.queryByRole("button", { name: "Salir de la mesa" })).not.toBeInTheDocument();
  });

  it("hands the game over to renderGame once it starts, and lets the player go back from there", async () => {
    const scenario = room();
    renderLobby(apiWith(scenario));
    await screen.findByRole("button", { name: "Empezar" });

    scenario.table = view({ status: "Playing", seats: [seat(0, { mine: true }), seat(1)] });
    expect(await screen.findByText("Jugando en Mi mesa (Playing)", {}, { timeout: 2_500 })).toBeInTheDocument();
    scenario.list = [];
    await userEvent.click(screen.getByRole("button", { name: "Salir al lobby" }));
    expect(await screen.findByRole("heading", { name: "Mesas abiertas" })).toBeInTheDocument();
  });

  it("tells the player when the table was cancelled", async () => {
    renderLobby(apiWith(room({ status: "Cancelled" })));
    expect(await screen.findByText(/La mesa se canceló/)).toBeInTheDocument();
  });

  it("tells the player when the table no longer exists for them", async () => {
    const api = apiWith(room(), {
      [`GET /games/uno/tables/${TABLE_ID}`]: () => {
        throw new ApiError(404, "RoundNotFound", "No existe la mesa.");
      },
    });
    renderLobby(api);

    expect(await screen.findByText("No existe la mesa.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Volver a las mesas" })).toBeInTheDocument();
  });

  it("explains in startBlocker what is missing", () => {
    expect(startBlocker({ minPlayers: 2, seats: [seat(0)] })).toMatch(/Faltan 1 jugador/);
    expect(startBlocker({ minPlayers: 2, seats: [seat(0), seat(1, { ready: false }), seat(2, { ready: false })] })).toMatch(/2 asientos/);
    expect(startBlocker({ minPlayers: 2, seats: [seat(0), seat(1)] })).toBeNull();
  });
});
