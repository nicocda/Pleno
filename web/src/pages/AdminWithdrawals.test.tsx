import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Me, Withdrawal } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { AdminWithdrawals } from "./AdminWithdrawals";

const account = { accountId: "a1", userId: "u1", available: 0, reserved: 0, version: 1, openReservations: {} };
const me = (roles: string[]): Me => ({ userId: "u1", displayName: "admin", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
const withdrawal = (overrides: Partial<Withdrawal> = {}): Withdrawal => ({
  id: "w1", playerUserId: "0a1b2c3d-0001-4000-8000-000000000001", playerName: null, amount: 200, status: "Pending", createdAt: "2026-10-10T10:00:00Z", resolvedAt: null, ...overrides,
});

describe("AdminWithdrawals", () => {
  it("refuses entry to someone without the backoffice role", async () => {
    const api = fakeApi({ "GET /me": () => me(["player"]), "GET /wallet/me": () => account });
    renderApp(<AdminWithdrawals />, { api });

    expect(await screen.findByText("No tenés permisos de administrador.")).toBeInTheDocument();
    expect(api.calls.some((c) => c.path.startsWith("/backoffice"))).toBe(false);
  });

  it("lists the withdrawals without a cashier and collects one", async () => {
    const api = fakeApi({
      "GET /me": () => me(["backoffice"]),
      "GET /wallet/me": () => account,
      "GET /backoffice/wallet/withdrawals": () => [withdrawal(), withdrawal({ id: "w2", amount: 70, playerName: "beto" })],
      "POST /backoffice/wallet/withdrawals/w1/pay": () => withdrawal({ status: "Paid" }),
    });
    renderApp(<AdminWithdrawals />, { api });

    expect(await screen.findByText("0a1b2c3d…0001")).toBeInTheDocument();
    expect(screen.getByText("beto")).toBeInTheDocument();
    await userEvent.click(screen.getAllByRole("button", { name: "Cobrar" })[0]!);

    await waitFor(() => expect(api.calls.some((c) => c.path === "/backoffice/wallet/withdrawals/w1/pay")).toBe(true));
    expect(await screen.findByText("Retiro cobrado: las fichas volvieron a la casa.")).toBeInTheDocument();
  });

  it("rejects a withdrawal and says when there is nothing pending", async () => {
    const api = fakeApi({
      "GET /me": () => me(["backoffice"]),
      "GET /wallet/me": () => account,
      "GET /backoffice/wallet/withdrawals": () => [withdrawal()],
      "POST /backoffice/wallet/withdrawals/w1/reject": () => withdrawal({ status: "Rejected" }),
    });
    renderApp(<AdminWithdrawals />, { api });

    await userEvent.click(await screen.findByRole("button", { name: "Rechazar" }));

    await waitFor(() => expect(api.calls.some((c) => c.path === "/backoffice/wallet/withdrawals/w1/reject")).toBe(true));
  });

  it("says so when there are no pending withdrawals", async () => {
    const api = fakeApi({ "GET /me": () => me(["backoffice"]), "GET /wallet/me": () => account, "GET /backoffice/wallet/withdrawals": () => [] });
    renderApp(<AdminWithdrawals />, { api });

    expect(await screen.findByText("No hay retiros pendientes.")).toBeInTheDocument();
  });
});
