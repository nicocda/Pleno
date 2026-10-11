import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { Me, SlotsSettings, SlotsSettingsBody, SlotsSettingsPreview, SlotsSettingsView } from "../api/types";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { AdminSlots, toBody } from "./AdminSlots";

const ME_ID = "5078cc64-5113-4eb1-ad1a-cf743f87acbb";
const me = (roles: string[]): Me => ({ userId: ME_ID, displayName: "nicocda", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
const account = { accountId: "a1", userId: ME_ID, available: 500, reserved: 0, version: 1, openReservations: {} };

const view = (overrides: Partial<SlotsSettingsView> = {}): SlotsSettingsView => ({
  symbols: [
    { name: "Cereza", weight: 20, triplePayout: 7 },
    { name: "Limon", weight: 16, triplePayout: 10 },
    { name: "Siete", weight: 3, triplePayout: 100 },
  ],
  leadingPays: [{ symbol: "Cereza", count: 1, payout: 1 }],
  maxStake: 10_000,
  totalWeight: 39,
  returnToPlayerPercent: 96.14,
  hitRatePercent: 33.7,
  ...overrides,
});
const settings = (overrides: Partial<SlotsSettings> = {}): SlotsSettings => ({ version: 0, current: view(), baseline: view(), ...overrides });
const valid = (rtp: number, hit = 30): SlotsSettingsPreview => ({ valid: true, error: null, returnToPlayerPercent: rtp, hitRatePercent: hit, totalWeight: 39 });

type Handlers = Record<string, (body?: unknown) => unknown>;

function apiWith(extra: Handlers = {}, roles = ["backoffice", "player"], current = settings()): FakeApi {
  return fakeApi({
    "GET /me": () => me(roles),
    "GET /wallet/me": () => account,
    "GET /backoffice/games/slots/settings": () => current,
    "GET /backoffice/games/slots/settings/history": () => [],
    "POST /backoffice/games/slots/settings/preview": () => valid(96.14),
    ...extra,
  });
}

const calls = (api: FakeApi, method: "GET" | "POST", path: string) => api.calls.filter((c) => c.method === method && c.path === path);
const previews = (api: FakeApi) => calls(api, "POST", "/backoffice/games/slots/settings/preview");

async function loaded() {
  await screen.findByRole("table", { name: "Símbolos" });
}

describe("AdminSlots", () => {
  it("refuses entry to someone without the backoffice role and never asks for the settings", async () => {
    const api = apiWith({}, ["player"]);
    renderApp(<AdminSlots />, { api });

    expect(await screen.findByText("No tenés permisos de administrador.")).toBeInTheDocument();
    expect(api.calls.some((c) => c.path.startsWith("/backoffice"))).toBe(false);
  });

  it("shows the current table, its version and the return it gives", async () => {
    renderApp(<AdminSlots />, { api: apiWith() });
    await loaded();

    expect(screen.getByLabelText("Peso de Cereza")).toHaveValue("20");
    expect(screen.getByLabelText("Premio de Siete")).toHaveValue("100");
    expect(screen.getByLabelText("Apuesta máxima por giro (fichas)")).toHaveValue("10000");
    expect(screen.getByText(/Versión vigente/)).toHaveTextContent("0 (la de la configuración)");
    expect(await screen.findByTestId("slots-preview")).toHaveTextContent("96,14 %");
  });

  it("tests what is being typed (without saving) and shows the new return next to the current one", async () => {
    const api = apiWith({
      "POST /backoffice/games/slots/settings/preview": (body) => valid((body as SlotsSettingsBody).symbols[0]!.weight === 30 ? 88.5 : 96.14, 41.2),
    });
    renderApp(<AdminSlots />, { api });
    await loaded();

    const weight = screen.getByLabelText("Peso de Cereza");
    await userEvent.clear(weight);
    await userEvent.type(weight, "30");

    await waitFor(() => expect(screen.getByTestId("slots-preview")).toHaveTextContent("88,5 %"), { timeout: 3_000 });
    expect(screen.getByTestId("slots-preview")).toHaveTextContent("vigente: 96,14 %");
    expect(screen.getByTestId("slots-preview")).toHaveTextContent("41,2 %");
    const last = previews(api).at(-1)!.body as SlotsSettingsBody;
    expect(last.symbols[0]).toEqual({ name: "Cereza", weight: 30, triplePayout: 7 });
    expect(calls(api, "POST", "/backoffice/games/slots/settings")).toHaveLength(0); // probar no publica
  });

  it("waits until the admin stops typing before asking the server", async () => {
    const api = apiWith();
    renderApp(<AdminSlots />, { api });
    await loaded();
    await screen.findByTestId("slots-preview");
    const before = previews(api).length;

    await userEvent.type(screen.getByLabelText("Peso de Cereza"), "123456"); // seis teclas seguidas

    await waitFor(() => expect(previews(api).length).toBeGreaterThan(before), { timeout: 3_000 });
    expect(previews(api).length - before).toBeLessThanOrEqual(2); // no una consulta por tecla
  });

  it("explains why a table is not valid and will not let it be published", async () => {
    const api = apiWith({
      "POST /backoffice/games/slots/settings/preview": (body) =>
        (body as SlotsSettingsBody).symbols[2]!.triplePayout === 5_000
          ? ({ valid: false, error: "El retorno al jugador debe ser mayor que 0 y no superar el 100% (es 480,00%).", returnToPlayerPercent: null, hitRatePercent: null, totalWeight: null } satisfies SlotsSettingsPreview)
          : valid(96.14),
    });
    renderApp(<AdminSlots />, { api });
    await loaded();

    const prize = screen.getByLabelText("Premio de Siete");
    await userEvent.clear(prize);
    await userEvent.type(prize, "5000");

    expect(await screen.findByText(/no superar el 100%/, {}, { timeout: 3_000 })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publicar" })).toBeDisabled();
  });

  it("does not ask the server when a field is not a whole number", async () => {
    const api = apiWith();
    renderApp(<AdminSlots />, { api });
    await loaded();
    await screen.findByTestId("slots-preview");
    const before = previews(api).length;

    await userEvent.clear(screen.getByLabelText("Peso de Limon"));
    await userEvent.type(screen.getByLabelText("Peso de Limon"), "1.5");

    expect(await screen.findByText(/números enteros/)).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(previews(api).length).toBe(before);
    expect(screen.getByRole("button", { name: "Publicar" })).toBeDisabled();
  });

  it("only allows publishing when something changed, and sends the version it was looking at", async () => {
    const api = apiWith(
      { "POST /backoffice/games/slots/settings": () => settings({ version: 4 }) },
      ["backoffice", "player"],
      settings({ version: 3 }),
    );
    renderApp(<AdminSlots />, { api });
    await loaded();
    await screen.findByTestId("slots-preview");
    expect(screen.getByRole("button", { name: "Publicar" })).toBeDisabled(); // sin cambios

    const stake = screen.getByLabelText("Apuesta máxima por giro (fichas)");
    await userEvent.clear(stake);
    await userEvent.type(stake, "500");
    await waitFor(() => expect(screen.getByRole("button", { name: "Publicar" })).toBeEnabled(), { timeout: 3_000 });
    await userEvent.click(screen.getByRole("button", { name: "Publicar" }));

    expect(await screen.findByText("Se publicó la versión 4.")).toBeInTheDocument();
    const sent = calls(api, "POST", "/backoffice/games/slots/settings")[0]!.body as SlotsSettingsBody;
    expect(sent.baseVersion).toBe(3);
    expect(sent.maxStake).toBe(500);
    expect(sent.symbols).toHaveLength(3);
  });

  it("tells the admin when someone else published first, instead of overwriting", async () => {
    const api = apiWith({
      "POST /backoffice/games/slots/settings": () => {
        throw new ApiError(409, "SettingsConflict", "Otra persona ya publicó la versión 2. Recargá los ajustes y volvé a intentar.");
      },
    });
    renderApp(<AdminSlots />, { api });
    await loaded();
    const stake = screen.getByLabelText("Apuesta máxima por giro (fichas)");
    await userEvent.clear(stake);
    await userEvent.type(stake, "500");
    await waitFor(() => expect(screen.getByRole("button", { name: "Publicar" })).toBeEnabled(), { timeout: 3_000 });

    await userEvent.click(screen.getByRole("button", { name: "Publicar" }));

    expect(await screen.findByText(/Otra persona ya publicó/)).toBeInTheDocument();
  });

  it("adds and removes symbols, keeping at least two, and drops the streak prizes of a removed symbol", async () => {
    renderApp(<AdminSlots />, { api: apiWith() });
    await loaded();
    const symbols = () => within(screen.getByRole("table", { name: "Símbolos" })).getAllByRole("row").length - 1;
    expect(symbols()).toBe(3);

    await userEvent.click(screen.getByRole("button", { name: "Agregar símbolo" }));
    expect(symbols()).toBe(4);
    await userEvent.click(screen.getByRole("button", { name: "Quitar símbolo 4" }));
    await userEvent.click(screen.getByRole("button", { name: "Quitar Cereza" })); // tenia un premio por racha
    expect(symbols()).toBe(2);
    expect(screen.queryByLabelText("Premio de la racha 1")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Quitar Limon" })).toBeDisabled(); // minimo dos
    expect(screen.getByRole("button", { name: "Quitar Siete" })).toBeDisabled();
  });

  it("goes back to the values in the configuration, or discards the edits", async () => {
    const current = settings({ version: 2, current: view({ maxStake: 500 }), baseline: view({ maxStake: 10_000 }) });
    renderApp(<AdminSlots />, { api: apiWith({}, ["backoffice", "player"], current) });
    await loaded();
    const stake = screen.getByLabelText("Apuesta máxima por giro (fichas)");
    expect(stake).toHaveValue("500");

    await userEvent.click(screen.getByRole("button", { name: "Cargar los valores de la configuración" }));
    expect(stake).toHaveValue("10000");

    await userEvent.click(screen.getByRole("button", { name: "Descartar cambios" }));
    expect(stake).toHaveValue("500");
  });

  it("warns when the return is unusually low", async () => {
    renderApp(<AdminSlots />, { api: apiWith({ "POST /backoffice/games/slots/settings/preview": () => valid(60) }) });
    await loaded();

    expect(await screen.findByText(/es un retorno bajo/)).toBeInTheDocument();
  });

  it("lists the published versions with who published them", async () => {
    const api = apiWith({
      "GET /backoffice/games/slots/settings/history": () => [
        { version: 2, changedBy: ME_ID, changedAt: "2026-10-09T12:00:00Z", returnToPlayerPercent: 94.5, hitRatePercent: 30, maxStake: 500, symbols: 3 },
        { version: 1, changedBy: "0a1b2c3d-0001-4000-8000-000000000001", changedAt: "2026-10-08T12:00:00Z", returnToPlayerPercent: 96.14, hitRatePercent: 33.7, maxStake: 10_000, symbols: 6 },
      ],
    });
    renderApp(<AdminSlots />, { api });

    const table = await screen.findByRole("table", { name: "Versiones publicadas" });
    expect(within(table).getAllByRole("row")).toHaveLength(3);
    expect(table).toHaveTextContent("94,5 %");
    expect(table).toHaveTextContent("0a1b2c3d…0001");
  });
});

describe("toBody", () => {
  const draft = { symbols: [{ name: " A ", weight: "3", triple: "2" }, { name: "B", weight: "1", triple: "4" }], leading: [{ symbol: "A", count: "1", payout: "1" }], maxStake: "100" };

  it("turns what was typed into numbers and trims the names", () => {
    expect(toBody(draft, 7)).toEqual({
      symbols: [{ name: "A", weight: 3, triplePayout: 2 }, { name: "B", weight: 1, triplePayout: 4 }],
      leadingPays: [{ symbol: "A", count: 1, payout: 1 }],
      maxStake: 100,
      baseVersion: 7,
    });
  });

  it("refuses empty names, decimals, negatives and text", () => {
    expect(toBody({ ...draft, symbols: [{ name: "", weight: "1", triple: "1" }, draft.symbols[1]!] }, 0)).toBeNull();
    expect(toBody({ ...draft, maxStake: "1.5" }, 0)).toBeNull();
    expect(toBody({ ...draft, maxStake: "-3" }, 0)).toBeNull();
    expect(toBody({ ...draft, symbols: [{ name: "A", weight: "abc", triple: "1" }, draft.symbols[1]!] }, 0)).toBeNull();
    expect(toBody({ ...draft, leading: [{ symbol: "A", count: "", payout: "1" }] }, 0)).toBeNull();
  });
});
