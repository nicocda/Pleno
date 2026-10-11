import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { Paytable, PlacedBet, Spin } from "../api/types";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Slots } from "./Slots";

const account = (available: number) => ({ accountId: "a1", userId: "u1", available, reserved: 0, version: 2, openReservations: {} });
const placed = (betId: string): PlacedBet => ({ betId, status: "Placed", nonce: 0, commitment: "c".repeat(64), clientSeed: "abc", alreadyPlaced: false });

const paytable: Paytable = {
  reels: 3,
  symbols: [
    { name: "Cereza", weight: 20, triplePayout: 7 },
    { name: "Limon", weight: 16, triplePayout: 10 },
    { name: "Naranja", weight: 12, triplePayout: 14 },
    { name: "Campana", weight: 8, triplePayout: 25 },
    { name: "Bar", weight: 5, triplePayout: 50 },
    { name: "Siete", weight: 3, triplePayout: 100 },
  ],
  leadingPays: [
    { symbol: "Cereza", count: 2, payout: 3 },
    { symbol: "Cereza", count: 1, payout: 1 },
  ],
  minStake: 1,
  maxStake: 10_000,
  totalWeight: 64,
  returnToPlayerPercent: 96.14,
  hitRatePercent: 33.72,
  cascadeMultipliers: [1, 2, 3, 5, 10],
  cascadeMinPay: 2,
};

const spinState = (overrides: Partial<Spin> = {}): Spin => ({
  betId: "bet-1", status: "Settled", stake: 10, reels: ["Limon", "Bar", "Siete"], multiplier: 0, payout: 0,
  pairId: "p", nonce: 0, failureReason: null, placedAt: "2026-10-09T12:00:00Z", ...overrides,
});

function apiWith(extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}, available = 1000): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account(available),
    "GET /games/slots/paytable": () => paytable,
    "GET /games/slots/spins": () => [],
    "POST /games/slots/spins": () => placed("bet-1"),
    "GET /games/slots/spins/bet-1": () => spinState(),
    ...extra,
  });
}

const spinCalls = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === "/games/slots/spins");

/** Simula la preferencia del sistema "reducir movimiento": los rodillos se asientan al instante y las pruebas no esperan la animacion. */
function reduceMotion(reduce: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: reduce && query.includes("prefers-reduced-motion"),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    onchange: null,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}

async function ready() {
  await waitFor(() => expect(screen.getByRole("button", { name: /Girar por/ })).toBeEnabled());
}

const apiWithspin = (spin: Partial<Spin>) => apiWith({ "GET /games/slots/spins/bet-1": () => spinState(spin) });

const symbolsShown = () => [0, 1, 2].map((i) => screen.getByTestId(`reel-${i}`).getAttribute("data-symbol"));

describe("Slots", () => {
  beforeEach(() => {
    reduceMotion(true);
    window.localStorage.setItem("pleno.slots.theme", "clasico"); // estos tests miran los nombres del servidor; el tema Gemas se prueba aparte
  });

  it("shows the public paytable and the theoretical return", async () => {
    renderApp(<Slots />, { api: apiWith() });

    expect(await screen.findByText(/Retorno teórico 96,14 %/)).toBeInTheDocument();
    const prizes = screen.getByRole("list", { name: "Premios" });
    expect(prizes).toHaveTextContent("x100");
    expect(prizes).toHaveTextContent("x3");
    expect(prizes).toHaveTextContent("Cereza en el primer rodillo");
  });

  it("spins with an idempotency key, stops the reels on what the server drew and shows the result", async () => {
    const api = apiWith({
      "GET /games/slots/spins/bet-1": () => spinState({ stake: 50, reels: ["Limon", "Limon", "Limon"], multiplier: 10, payout: 500 }),
    });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("radio", { name: "50" }));
    await userEvent.click(screen.getByRole("button", { name: /Girar por 50 fichas/ }));

    await waitFor(() => expect(spinCalls(api)).toHaveLength(1));
    expect(spinCalls(api)[0]!.body).toEqual({ stake: 50 });
    expect(spinCalls(api)[0]!.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);

    expect(await screen.findByRole("dialog", { name: "¡GRAN PREMIO!" })).toBeInTheDocument(); // x10
    expect(within(screen.getByRole("dialog")).getByText(/450 fichas/)).toBeInTheDocument(); // ganancia neta: 500 - 50
    await waitFor(() => expect(symbolsShown()).toEqual(["Limon", "Limon", "Limon"])); // ReelsView copia el modelo en un requestAnimationFrame
    expect(screen.getByTestId("reels")).toHaveAttribute("data-state", "settled");
  });

  it("plays automatically until stopped, using a different idempotency key per spin", async () => {
    let n = 0;
    const api = apiWith({
      "POST /games/slots/spins": () => placed(`bet-${++n}`),
      "GET /games/slots/spins/bet-1": () => spinState({ betId: "bet-1" }),
      "GET /games/slots/spins/bet-2": () => spinState({ betId: "bet-2" }),
      "GET /games/slots/spins/bet-3": () => spinState({ betId: "bet-3" }),
    });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "10 giros" }));
    await waitFor(() => expect(spinCalls(api).length).toBeGreaterThanOrEqual(2), { timeout: 4000 });
    await userEvent.click(await screen.findByRole("button", { name: /Detener/ }));

    const keys = spinCalls(api).map((c) => c.headers?.["Idempotency-Key"]);
    expect(new Set(keys).size).toBe(keys.length);
    expect(await screen.findByRole("button", { name: "10 giros" })).toBeInTheDocument();
  });

  it("disables automatic play options the balance cannot cover", async () => {
    renderApp(<Slots />, { api: apiWith({}, 300) }); // 300 fichas a 10 por giro = 30 giros
    await ready();

    expect(screen.getByRole("button", { name: "10 giros" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "25 giros" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "50 giros" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "100 giros" })).toBeDisabled();
    expect(screen.getByText(/te alcanzan para 30 giros/)).toBeInTheDocument();
  });

  it("explains why automatic play is unavailable when the balance does not reach the minimum", async () => {
    renderApp(<Slots />, { api: apiWith({}, 50) }); // 5 giros a 10: menos que la opcion minima
    await waitFor(() => expect(screen.getByRole("button", { name: /Girar por/ })).toBeEnabled());

    expect(screen.getByRole("button", { name: "10 giros" })).toBeDisabled();
    expect(screen.getByText(/Juego automático no disponible/)).toBeInTheDocument();
  });

  it("shows a loss without celebrating", async () => {
    renderApp(<Slots />, { api: apiWith() });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByText("No hubo suerte esta vez")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await waitFor(() => expect(symbolsShown()).toEqual(["Limon", "Bar", "Siete"])); // ReelsView copia el modelo en un requestAnimationFrame
  });

  it("tells the player when a spin only gives the stake back", async () => {
    const api = apiWith({ "GET /games/slots/spins/bet-1": () => spinState({ reels: ["Cereza", "Limon", "Bar"], multiplier: 1, payout: 10 }) });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByText("Recuperaste tus fichas")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("explains a rejected spin in plain language and stops the reels", async () => {
    const api = apiWith({
      "GET /games/slots/spins/bet-1": () => spinState({ status: "Rejected", reels: [], multiplier: null, payout: null, failureReason: "InsufficientFunds" }),
    });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByText("Giro rechazado")).toBeInTheDocument();
    expect(screen.getAllByText(/No te alcanzan las fichas/).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByTestId("reels")).toHaveAttribute("data-state", "idle"));
  });

  it("says the stake came back when a spin is voided", async () => {
    const api = apiWith({
      "GET /games/slots/spins/bet-1": () => spinState({ status: "Voided", multiplier: 100, payout: 1000, failureReason: "ReservationExpired" }),
    });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByText("Giro anulado")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // aunque se haya sorteado un premio, anulado no paga
  });

  it("will not let the player spin for more than they have or an invalid amount", async () => {
    renderApp(<Slots />, { api: apiWith({}, 30) });
    await ready();

    await userEvent.click(screen.getByRole("radio", { name: "50" }));
    expect(screen.getByRole("button", { name: /Girar por 50 fichas/ })).toBeDisabled();
    expect(await screen.findByText(/No te alcanzan las fichas/)).toBeInTheDocument();

    const input = screen.getByLabelText(/Otro monto/);
    await userEvent.clear(input);
    await userEvent.type(input, "0");
    expect(screen.getByRole("button", { name: /Girar por/ })).toBeDisabled();
    await userEvent.clear(input);
    await userEvent.type(input, "20000");
    expect(screen.getByRole("button", { name: /Girar por/ })).toBeDisabled();
  });

  it("locks the amount while the reels are spinning", async () => {
    const api = apiWith({ "GET /games/slots/spins/bet-1": () => spinState({ status: "Placed", reels: [], multiplier: null, payout: null }) });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByRole("button", { name: "Girando…" })).toBeDisabled();
    expect(screen.getByRole("radio", { name: "50" })).toBeDisabled();
  });

  it("reuses the same idempotency key when the player retries after a network failure, so the server never charges twice", async () => {
    let attempt = 0;
    const api = apiWith({
      "POST /games/slots/spins": () => {
        attempt += 1;
        if (attempt === 1) throw new ApiError(0, "NetworkError", "sin red");
        return placed("bet-1");
      },
    });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));
    expect(await screen.findByText(/No hay conexión con el servidor/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("reels")).toHaveAttribute("data-state", "idle")); // no quedan girando en falso
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    await waitFor(() => expect(spinCalls(api)).toHaveLength(2));
    const [first, second] = spinCalls(api);
    expect(second!.headers?.["Idempotency-Key"]).toBe(first!.headers?.["Idempotency-Key"]);
  });

  it("uses a fresh key for the next spin once the previous one was accepted", async () => {
    const api = apiWith();
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));
    await screen.findByText("No hubo suerte esta vez");
    await ready();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    await waitFor(() => expect(spinCalls(api)).toHaveLength(2));
    const [first, second] = spinCalls(api);
    expect(second!.headers?.["Idempotency-Key"]).not.toBe(first!.headers?.["Idempotency-Key"]);
  });

  it("does not reuse the key after a definitive server rejection", async () => {
    const api = apiWith({ "POST /games/slots/spins": () => { throw new ApiError(400, "InvalidBet", "x"); } });
    renderApp(<Slots />, { api });
    await ready();

    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));
    expect(await screen.findByText(/Esa apuesta no es válida/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    await waitFor(() => expect(spinCalls(api)).toHaveLength(2));
    const [first, second] = spinCalls(api);
    expect(second!.headers?.["Idempotency-Key"]).not.toBe(first!.headers?.["Idempotency-Key"]);
  });

  it("speeds up the check when the live notice arrives", async () => {
    let state: Spin = spinState({ status: "Placed", reels: [], multiplier: null, payout: null });
    const api = apiWith({ "GET /games/slots/spins/bet-1": () => state });
    const { hub } = renderApp(<Slots />, { api });
    await ready();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));
    await screen.findByRole("button", { name: "Girando…" });

    state = spinState({ reels: ["Siete", "Siete", "Siete"], multiplier: 100, payout: 1000 });
    act(() => hub.emit("roundClosed", { betId: "bet-1", game: "Slots", status: "Settled", winningNumber: null, stake: 10, payout: 1000, failureReason: null }));

    expect(await screen.findByRole("dialog", { name: "¡MEGA PREMIO!" }, { timeout: 4_000 })).toBeInTheDocument();
  });
});

describe("Slots animation", () => {
  beforeEach(() => {
    reduceMotion(false);
    window.localStorage.setItem("pleno.slots.theme", "clasico");
  });

  it("does not reveal the result until the last reel has stopped", async () => {
    const api = apiWith({ "GET /games/slots/spins/bet-1": () => spinState({ reels: ["Siete", "Siete", "Siete"], multiplier: 100, payout: 1000 }) });
    renderApp(<Slots />, { api });
    await ready();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));
    await screen.findByRole("button", { name: "Girando…" });

    // El servidor ya decidio, pero los rodillos todavia giran: no se adelanta nada.
    await act(() => new Promise((resolve) => setTimeout(resolve, 600)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByTestId("reels")).toHaveAttribute("data-state", "spinning");

    // Cuando frenan, recien ahi aparece.
    expect(await screen.findByRole("dialog", { name: "¡MEGA PREMIO!" }, { timeout: 6_000 })).toBeInTheDocument();
    await waitFor(() => expect(symbolsShown()).toEqual(["Siete", "Siete", "Siete"])); // ReelsView copia el modelo en un requestAnimationFrame
  }, 12_000);

  it("plays the cascades one by one before revealing the result, ending on the last combination", async () => {
    const steps = [
      { reels: ["Cereza", "Cereza", "Bar"], pay: 3, multiplier: 1 },
      { reels: ["Limon", "Bar", "Bar"], pay: 0, multiplier: 2 },
    ];
    const api = apiWithspin({ reels: ["Cereza", "Cereza", "Bar"], steps, multiplier: 3, payout: 30 });
    renderApp(<Slots />, { api });
    await ready();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    // Mientras explota y cae lo nuevo aparece el multiplicador de la cascada y todavia no hay resultado.
    expect(await screen.findByTestId("cascade-badge", {}, { timeout: 8_000 })).toHaveTextContent("x2");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await waitFor(() => expect(symbolsShown()).toEqual(["Limon", "Bar", "Bar"]));

    expect(await screen.findByRole("dialog", { name: "¡Ganaste!" }, { timeout: 5_000 })).toHaveTextContent("1 cascada encadenadas");
    expect(symbolsShown()).toEqual(["Limon", "Bar", "Bar"]); // se queda la combinacion final, no vuelve a la inicial
  }, 20_000);
});

describe("Slots themes", () => {
  beforeEach(() => reduceMotion(true));

  it("draws the symbols as gems by default and lets the player go back to the classic style", async () => {
    renderApp(<Slots />, { api: apiWith() });
    await ready();

    expect(screen.getByTestId("reels")).toHaveClass("reels--gemas");
    expect(screen.getByTestId("reel-0").querySelector("svg.gem")).not.toBeNull();
    expect(screen.getByRole("radio", { name: "Gemas" })).toBeChecked();
    expect(screen.getByLabelText("Premios")).toHaveTextContent("Tres Diamante"); // el lector de pantalla oye el nombre de la gema

    await userEvent.click(screen.getByRole("radio", { name: "Clásico" }));

    expect(screen.getByTestId("reels")).toHaveClass("reels--clasico");
    expect(screen.getByTestId("reel-0").querySelector("svg.gem")).toBeNull();
    expect(window.localStorage.getItem("pleno.slots.theme")).toBe("clasico"); // y se acuerda la proxima vez
  });

  it("celebrates according to the size of the prize", async () => {
    const api = apiWith({ "GET /games/slots/spins/bet-1": () => spinState({ stake: 10, reels: ["Cereza", "Cereza", "Cereza"], multiplier: 7, payout: 70 }) });
    renderApp(<Slots />, { api });
    await ready();
    await userEvent.click(screen.getByRole("button", { name: /Girar por/ }));

    expect(await screen.findByRole("dialog", { name: "¡Ganaste!" })).toHaveClass("win-modal--win");
    expect(screen.getByRole("dialog")).toHaveTextContent("60 fichas"); // ganancia neta: 70 - 10
  });
});
