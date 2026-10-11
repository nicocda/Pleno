import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../api/client";
import type { Withdrawal } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { Withdraw } from "./Withdraw";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 2, openReservations: {} };

const withdrawal = (overrides: Partial<Withdrawal> = {}): Withdrawal => ({
  id: "w1", playerUserId: "u1", amount: 300, status: "Pending", createdAt: "2026-10-10T10:00:00Z", resolvedAt: null, ...overrides,
});

describe("Withdraw", () => {
  it("explains what happens, asks for the amount with an idempotency key and says the chips are set aside", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /wallet/withdrawals": () => [],
      "POST /wallet/withdrawals": () => withdrawal(),
    });
    renderApp(<Withdraw />, { api });

    expect(await screen.findByText("apartan", { selector: "strong" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/Fichas a retirar/), "300");
    await userEvent.click(screen.getByRole("button", { name: "Pedir retiro" }));

    await waitFor(() => expect(api.calls.some((c) => c.method === "POST" && c.path === "/wallet/withdrawals")).toBe(true));
    const call = api.calls.find((c) => c.method === "POST" && c.path === "/wallet/withdrawals")!;
    expect(call.body).toEqual({ amount: 300 });
    expect(call.headers?.["Idempotency-Key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(await screen.findByText(/quedan apartadas hasta que tu cajero las cobre/)).toBeInTheDocument();
  });

  it("does not let the player ask for less than the minimum or more than the balance", async () => {
    renderApp(<Withdraw />, { api: fakeApi({ "GET /wallet/me": () => account, "GET /wallet/withdrawals": () => [] }) });

    await userEvent.type(await screen.findByLabelText(/Fichas a retirar/), "5");
    expect(screen.getByRole("button", { name: "Pedir retiro" })).toBeDisabled();
    await userEvent.clear(screen.getByLabelText(/Fichas a retirar/));
    await userEvent.type(screen.getByLabelText(/Fichas a retirar/), "5000");

    expect(await screen.findByText("No te alcanzan las fichas para ese retiro.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Pedir retiro" })).toBeDisabled();
  });

  it("lists the requests with their state and lets the player cancel only a pending one", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /wallet/withdrawals": () => [withdrawal(), withdrawal({ id: "w2", amount: 50, status: "Paid" }), withdrawal({ id: "w3", amount: 70, status: "Expired" })],
      "POST /wallet/withdrawals/w1/cancel": () => withdrawal({ status: "Cancelled" }),
    });
    renderApp(<Withdraw />, { api });

    expect(await screen.findByText("Esperando a tu cajero")).toBeInTheDocument();
    expect(screen.getByText("Cobrado")).toBeInTheDocument();
    expect(screen.getByText("Venció: las fichas volvieron")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Cancelar" })).toHaveLength(1);

    await userEvent.click(screen.getByRole("button", { name: "Cancelar" }));

    await waitFor(() => expect(api.calls.some((c) => c.path === "/wallet/withdrawals/w1/cancel")).toBe(true));
    expect(await screen.findByText("Cancelaste el retiro: las fichas volvieron.")).toBeInTheDocument();
  });

  it("shows the server error when the request is refused", async () => {
    const api = fakeApi({
      "GET /wallet/me": () => account,
      "GET /wallet/withdrawals": () => [],
      "POST /wallet/withdrawals": () => {
        throw new ApiError(422, "InsufficientFunds", "x");
      },
    });
    renderApp(<Withdraw />, { api });

    await userEvent.type(await screen.findByLabelText(/Fichas a retirar/), "100");
    await userEvent.click(screen.getByRole("button", { name: "Pedir retiro" }));

    expect(await screen.findByText("No te alcanzan las fichas para ese retiro.")).toBeInTheDocument();
  });
});
