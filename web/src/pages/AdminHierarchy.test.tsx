import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Me } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { AdminHierarchy } from "./AdminHierarchy";

const ME_ID = "5078cc64-5113-4eb1-ad1a-cf743f87acbb";
const HEAD_ID = "0a1b2c3d-0004-4000-8000-000000000004";
const CASHIER_ID = "0a1b2c3d-0005-4000-8000-000000000005";
const account = { accountId: "a1", userId: ME_ID, available: 0, reserved: 0, version: 1, openReservations: {} };

const me = (roles: string[]): Me => ({ userId: ME_ID, displayName: "admin", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
const users = [{ userId: HEAD_ID, registeredAt: "2026-10-01T10:00:00Z" }, { userId: CASHIER_ID, registeredAt: "2026-10-02T10:00:00Z" }];
const head = { userId: HEAD_ID, level: "head_cashier", parentUserId: null, updatedAt: "2026-10-10T10:00:00Z" };

describe("AdminHierarchy", () => {
  it("refuses entry to someone without the backoffice role", async () => {
    const api = fakeApi({ "GET /me": () => me(["player"]), "GET /wallet/me": () => account });
    renderApp(<AdminHierarchy />, { api });

    expect(await screen.findByText("No tenés permisos de administrador.")).toBeInTheDocument();
    expect(api.calls.some((c) => c.path.startsWith("/backoffice"))).toBe(false);
  });

  it("shows the tree and places a cashier under a head of cashiers", async () => {
    const api = fakeApi({
      "GET /me": () => me(["backoffice"]),
      "GET /wallet/me": () => account,
      "GET /backoffice/users": () => users,
      "GET /backoffice/wallet/hierarchy": () => [head],
      [`PUT /backoffice/wallet/hierarchy/${CASHIER_ID}`]: () => ({ ...head, userId: CASHIER_ID, level: "cashier", parentUserId: HEAD_ID }),
    });
    renderApp(<AdminHierarchy />, { api });

    expect(await screen.findByText("Jefe de cajeros")).toBeInTheDocument();
    await screen.findByRole("option", { name: "0a1b2c3d…0005" });
    await userEvent.selectOptions(screen.getByLabelText("Persona"), CASHIER_ID);
    await userEvent.selectOptions(await screen.findByLabelText(/Depende de/), HEAD_ID);
    await userEvent.click(screen.getByRole("button", { name: "Guardar" }));

    await waitFor(() => expect(api.calls.some((c) => c.method === "PUT")).toBe(true));
    expect(api.calls.find((c) => c.method === "PUT")!.body).toEqual({ level: "cashier", parentUserId: HEAD_ID });
  });

  it("does not ask for a parent for a head of cashiers and warns when there is nobody to depend on", async () => {
    const api = fakeApi({
      "GET /me": () => me(["backoffice"]),
      "GET /wallet/me": () => account,
      "GET /backoffice/users": () => users,
      "GET /backoffice/wallet/hierarchy": () => [],
      [`PUT /backoffice/wallet/hierarchy/${HEAD_ID}`]: () => head,
    });
    renderApp(<AdminHierarchy />, { api });

    expect(await screen.findByText("Primero ubicá a un jefe de cajeros.")).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Nivel"), "head_cashier");
    expect(screen.queryByLabelText(/Depende de/)).not.toBeInTheDocument();
    await screen.findByRole("option", { name: "0a1b2c3d…0004" });
    await userEvent.selectOptions(screen.getByLabelText("Persona"), HEAD_ID);
    await userEvent.click(screen.getByRole("button", { name: "Guardar" }));

    await waitFor(() => expect(api.calls.find((c) => c.method === "PUT")?.body).toEqual({ level: "head_cashier", parentUserId: null }));
  });
});
