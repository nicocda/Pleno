import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Movement, MovementsPage } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { Movements } from "./Movements";

const account = { accountId: "a1", userId: "u1", available: 1_450, reserved: 0, version: 5, openReservations: {} };
const move = (overrides: Partial<Movement>): Movement => ({
  version: 1, at: "2026-10-09T12:00:00Z", kind: "Credit", delta: 100, balanceAfter: 100, reference: null, ...overrides,
});

const rows = () => within(screen.getByRole("table", { name: "Movimientos de saldo" })).getAllByRole("row").slice(1);

describe("Movements", () => {
  it("lists every movement with its concept, the signed chips and the balance left", async () => {
    const page: MovementsPage = {
      items: [
        move({ version: 4, kind: "Prize", delta: 250, balanceAfter: 1_650 }),
        move({ version: 3, kind: "Stake", delta: -100, balanceAfter: 1_400 }),
        move({ version: 2, kind: "Credit", delta: 500, balanceAfter: 1_500 }),
        move({ version: 1, kind: "WelcomeBonus", delta: 1_000, balanceAfter: 1_000 }),
      ],
      nextBefore: null,
    };
    renderApp(<Movements />, { api: fakeApi({ "GET /wallet/me": () => account, "GET /wallet/me/movements": () => page }) });

    expect(await screen.findByText("Premio cobrado")).toBeInTheDocument();
    const [prize, stake, credit, welcome] = rows();
    expect(prize).toHaveTextContent("+250");
    expect(prize).toHaveTextContent("1.650");
    expect(stake).toHaveTextContent("−100");
    expect(stake).toHaveTextContent("Apuesta");
    expect(credit).toHaveTextContent("Carga de fichas");
    expect(welcome).toHaveTextContent("Fichas de bienvenida");
    expect(screen.queryByRole("button", { name: /anteriores/ })).not.toBeInTheDocument();
  });

  it("shows the game of a stake or prize, and a dash when there is none", async () => {
    const page: MovementsPage = {
      items: [
        move({ version: 3, kind: "Prize", delta: 250, gameId: "roulette" }),
        move({ version: 2, kind: "Stake", delta: -100, gameId: "roulette" }),
        move({ version: 1, kind: "Credit", delta: 500 }),
      ],
      nextBefore: null,
    };
    const games = [{ id: "roulette", name: "Ruleta europea", tagline: "", route: "/ruleta", glyph: "◎", resolution: "Server" }];
    renderApp(<Movements />, { api: fakeApi({ "GET /wallet/me": () => account, "GET /wallet/me/movements": () => page, "GET /games": () => games }) });

    await screen.findByRole("table", { name: "Movimientos de saldo" });
    const [prize, stake, credit] = rows();
    await waitFor(() => expect(prize).toHaveTextContent("Ruleta europea"));
    expect(stake).toHaveTextContent("Ruleta europea");
    expect(credit).toHaveTextContent("—");
  });

  it("colours gains and losses differently", async () => {
    const page: MovementsPage = { items: [move({ version: 2, delta: 50 }), move({ version: 1, kind: "Stake", delta: -20 })], nextBefore: null };
    renderApp(<Movements />, { api: fakeApi({ "GET /wallet/me": () => account, "GET /wallet/me/movements": () => page }) });

    await screen.findByRole("table", { name: "Movimientos de saldo" });
    const [gain, loss] = rows();
    expect(within(gain!).getByText("+50")).toHaveClass("delta--up");
    expect(within(loss!).getByText("−20")).toHaveClass("delta--down");
  });

  it("loads the previous page with the cursor the server gave", async () => {
    // La respuesta depende del cursor pedido (no de cuantas veces se consulta: el aviso de conexion tambien refresca).
    const api: ReturnType<typeof fakeApi> = fakeApi({
      "GET /wallet/me": () => account,
      "GET /wallet/me/movements": () =>
        api.calls.at(-1)!.path.includes("before=5")
          ? ({ items: [move({ version: 4, kind: "Refund", delta: 40 })], nextBefore: null } satisfies MovementsPage)
          : ({ items: [move({ version: 5, delta: 5 })], nextBefore: 5 } satisfies MovementsPage),
    });
    renderApp(<Movements />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Ver movimientos anteriores" }));

    await waitFor(() => expect(rows()).toHaveLength(2));
    expect(screen.getByText("Apuesta devuelta")).toBeInTheDocument();
    const paths = api.calls.filter((c) => c.path.startsWith("/wallet/me/movements")).map((c) => c.path);
    expect(paths).toContain("/wallet/me/movements?limit=30");
    expect(paths).toContain("/wallet/me/movements?limit=30&before=5");
    expect(screen.queryByRole("button", { name: /anteriores/ })).not.toBeInTheDocument();
  });

  it("says so when there are no movements yet", async () => {
    renderApp(<Movements />, { api: fakeApi({ "GET /wallet/me": () => account, "GET /wallet/me/movements": () => ({ items: [], nextBefore: null }) }) });

    expect(await screen.findByText("Todavía no hay movimientos.")).toBeInTheDocument();
  });
});
