import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { Me } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { Cashier } from "./Cashier";

const ME_ID = "5078cc64-5113-4eb1-ad1a-cf743f87acbb";
const PLAYER_ID = "0a1b2c3d-0001-4000-8000-000000000001";
const account = { accountId: "a1", userId: ME_ID, available: 1_000, reserved: 0, version: 2, openReservations: {} };

const me = (roles: string[]): Me => ({ userId: ME_ID, displayName: "caja", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
const place = (level: string, commissionPermille = 20) => ({ userId: ME_ID, level, parentUserId: null, commissionPermille });
const members = [
  { userId: PLAYER_ID, level: "player", displayName: "ana", available: 50, reserved: 0 },
  { userId: "0a1b2c3d-0002-4000-8000-000000000002", level: "player", displayName: null, available: null, reserved: null },
];

describe("Cashier", () => {
  it("refuses entry to someone who is not a cashier and never asks for the jurisdiction", async () => {
    const api = fakeApi({ "GET /me": () => me(["player"]), "GET /wallet/me": () => account });
    renderApp(<Cashier />, { api });

    expect(await screen.findByText("No sos cajero ni jefe de cajeros.")).toBeInTheDocument();
    expect(api.calls.some((c) => c.path.startsWith("/cashier"))).toBe(false);
  });

  it("tells a cashier with no place in the tree to ask the administrator", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => {
        throw new ApiError(404, "NotInJurisdiction", "x");
      },
    });
    renderApp(<Cashier />, { api });

    expect(await screen.findByText(/Todavía no te asignaron un lugar/)).toBeInTheDocument();
  });

  it("lists the players with their balance and loads chips from the cashier balance with an idempotency key", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("cashier"),
      "GET /cashier/members": () => members,
      "GET /cashier/transfers": () => [],
      "POST /cashier/transfers": () => ({ transactionId: "t1", isDuplicate: false, commission: 3 }),
    });
    renderApp(<Cashier />, { api });

    expect(await screen.findByText(/Tus jugadores/)).toBeInTheDocument();
    expect(await screen.findByText("sin cuenta todavía")).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: /ana/ }));
    await userEvent.type(screen.getByLabelText("Fichas a cargar"), "150");
    await userEvent.click(screen.getByRole("button", { name: "Cargar fichas" }));

    await waitFor(() => expect(api.calls.some((c) => c.method === "POST" && c.path === "/cashier/transfers")).toBe(true));
    const call = api.calls.find((c) => c.method === "POST" && c.path === "/cashier/transfers")!;
    expect(call.body).toEqual({ toUserId: PLAYER_ID, amount: 150 });
    expect(call.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(await screen.findByText("Cargaste 150 fichas. Ganaste 3 de comisión.")).toBeInTheDocument();
  });

  it("tells the cashier what commission they earn, and says nothing when the rate is zero", async () => {
    const routes = (permille: number) => ({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("cashier", permille),
      "GET /cashier/members": () => members,
      "GET /cashier/transfers": () => [],
    });
    const { unmount } = renderApp(<Cashier />, { api: fakeApi(routes(20)) });
    expect(await screen.findByText(/comisión del 2 %/)).toBeInTheDocument();
    unmount();

    renderApp(<Cashier />, { api: fakeApi(routes(0)) });
    await screen.findByText(/Tus jugadores/);
    expect(screen.queryByText(/comisión del/)).not.toBeInTheDocument();
  });

  it("tells people apart by user name (with the short id next to it) and falls back to the short id when the name is not known yet", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("cashier"),
      "GET /cashier/members": () => members,
      "GET /cashier/transfers": () => [
        { targetUserId: PLAYER_ID, targetName: "ana", amount: 20, transactionId: "t1", occurredAt: "2026-10-10T10:00:00Z" },
        { targetUserId: "0a1b2c3d-0009-4000-8000-000000000009", targetName: null, amount: 5, transactionId: "t2", occurredAt: "2026-10-10T09:00:00Z" },
      ],
    });
    renderApp(<Cashier />, { api });

    const named = await screen.findByRole("button", { name: /ana/ });
    expect(named).toHaveTextContent("0a1b2c3d…0001"); // el id queda al lado para distinguir a dos con el mismo nombre
    expect(screen.getByRole("button", { name: /0a1b2c3d…0002/ })).toBeInTheDocument(); // sin nombre: el id
    expect(await screen.findByText("0a1b2c3d…0009")).toBeInTheDocument(); // una carga a alguien que ya no esta a mi cargo: sin nombre
    expect(screen.getAllByText("ana").length).toBeGreaterThan(1); // en la lista y en las ultimas cargas
  });

  it("lists the pending withdrawals of their people and collects or rejects them", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("cashier"),
      "GET /cashier/members": () => members,
      "GET /cashier/transfers": () => [],
      "GET /cashier/withdrawals": () => [
        { id: "w1", playerUserId: PLAYER_ID, playerName: "ana", amount: 300, status: "Pending", createdAt: "2026-10-10T10:00:00Z", resolvedAt: null },
        { id: "w2", playerUserId: "0a1b2c3d-0002-4000-8000-000000000002", playerName: null, amount: 40, status: "Pending", createdAt: "2026-10-10T11:00:00Z", resolvedAt: null },
      ],
      "POST /cashier/withdrawals/w1/pay": () => ({ id: "w1", status: "Paid" }),
      "POST /cashier/withdrawals/w2/reject": () => ({ id: "w2", status: "Rejected" }),
    });
    renderApp(<Cashier />, { api });

    expect(await screen.findByText("Retiros pendientes")).toBeInTheDocument();
    expect(await screen.findByText("300 fichas")).toBeInTheDocument();
    await userEvent.click(screen.getAllByRole("button", { name: "Cobrar" })[0]!);
    await waitFor(() => expect(api.calls.some((c) => c.path === "/cashier/withdrawals/w1/pay")).toBe(true));
    expect(await screen.findByText("Cobraste 300 fichas.")).toBeInTheDocument();

    await userEvent.click(screen.getAllByRole("button", { name: "Rechazar" })[1]!);
    await waitFor(() => expect(api.calls.some((c) => c.path === "/cashier/withdrawals/w2/reject")).toBe(true));
  });

  it("does not let a cashier load more chips than the balance", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("cashier"),
      "GET /cashier/members": () => members,
      "GET /cashier/transfers": () => [],
    });
    renderApp(<Cashier />, { api });

    await userEvent.click(await screen.findByRole("button", { name: /ana/ }));
    await userEvent.type(screen.getByLabelText("Fichas a cargar"), "5000");

    expect(await screen.findByText("No te alcanzan las fichas para esa carga.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cargar fichas" })).toBeDisabled();
  });

  it("says cashiers instead of players for the head of cashiers", async () => {
    const api = fakeApi({
      "GET /me": () => me(["player", "head_cashier"]),
      "GET /wallet/me": () => account,
      "GET /cashier/me": () => place("head_cashier"),
      "GET /cashier/members": () => [{ userId: PLAYER_ID, level: "cashier", available: 10, reserved: 0 }],
      "GET /cashier/transfers": () => [],
    });
    renderApp(<Cashier />, { api });

    expect(await screen.findByText("Tus cajeros")).toBeInTheDocument();
    expect(screen.getByText("Jefe de cajeros", { selector: "p" })).toBeInTheDocument();
  });
});
