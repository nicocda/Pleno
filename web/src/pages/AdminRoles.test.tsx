import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { IdentityUser, Me } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { AdminRoles } from "./AdminRoles";

const ME_ID = "0a1b2c3d-0003-4000-8000-000000000003";
const ANA_ID = "0a1b2c3d-0005-4000-8000-000000000005";
const account = { accountId: "a1", userId: ME_ID, available: 0, reserved: 0, version: 1, openReservations: {} };

const me = (roles: string[]): Me => ({ userId: ME_ID, displayName: "backoffice1", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
const accounts: IdentityUser[] = [
  { id: ME_ID, alias: "backoffice1", roles: ["backoffice", "player"], emailConfirmed: true, createdAt: "2026-10-01T10:00:00Z" },
  { id: ANA_ID, alias: "ana_juega", roles: ["player"], emailConfirmed: false, createdAt: "2026-10-02T10:00:00Z" },
];

describe("AdminRoles", () => {
  it("refuses entry to someone without the backoffice role and does not ask the identity service", async () => {
    const api = fakeApi({ "GET /me": () => me(["player"]), "GET /wallet/me": () => account });
    const identityApi = fakeApi({});
    renderApp(<AdminRoles />, { api, identityApi });

    expect(await screen.findByText("No tenés permisos de administrador.")).toBeInTheDocument();
    expect(identityApi.calls).toHaveLength(0);
  });

  it("lists accounts by alias, never showing an email", async () => {
    const api = fakeApi({ "GET /me": () => me(["backoffice"]), "GET /wallet/me": () => account });
    const identityApi = fakeApi({ "GET /identity/admin/users": () => accounts });
    renderApp(<AdminRoles />, { api, identityApi });

    expect(await screen.findByText("ana_juega")).toBeInTheDocument();
    expect(screen.getByText(/email sin verificar/)).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("@");
  });

  it("gives the cashier role and sends the new set of roles", async () => {
    const api = fakeApi({ "GET /me": () => me(["backoffice"]), "GET /wallet/me": () => account });
    const identityApi = fakeApi({
      "GET /identity/admin/users": () => accounts,
      [`PUT /identity/admin/users/${ANA_ID}/roles`]: () => undefined,
    });
    renderApp(<AdminRoles />, { api, identityApi });

    await screen.findByText("ana_juega");
    const save = screen.getAllByRole("button", { name: "Guardar" })[1]!;
    expect(save).toBeDisabled(); // sin cambios no se guarda
    await userEvent.click(screen.getByLabelText("Cajero para ana_juega"));
    await userEvent.click(save);

    await waitFor(() => expect(identityApi.calls.some((c) => c.method === "PUT")).toBe(true));
    expect(identityApi.calls.find((c) => c.method === "PUT")!.body).toEqual({ roles: ["cashier"] });
  });

  it("does not let an administrator remove their own administrator role", async () => {
    const api = fakeApi({ "GET /me": () => me(["backoffice"]), "GET /wallet/me": () => account });
    const identityApi = fakeApi({ "GET /identity/admin/users": () => accounts });
    renderApp(<AdminRoles />, { api, identityApi });

    await screen.findByText("backoffice1");

    expect(screen.getByLabelText("Administrador para backoffice1")).toBeDisabled();
    expect(screen.getByLabelText("Administrador para ana_juega")).toBeEnabled();
  });

  it("filters by the start of the alias", async () => {
    const api = fakeApi({ "GET /me": () => me(["backoffice"]), "GET /wallet/me": () => account });
    const identityApi = fakeApi({ "GET /identity/admin/users": () => accounts });
    renderApp(<AdminRoles />, { api, identityApi });
    await screen.findByText("ana_juega");

    await userEvent.type(screen.getByLabelText("Alias (empieza con…)"), "ana");

    await waitFor(() => expect(identityApi.calls.some((c) => c.path === "/identity/admin/users?q=ana")).toBe(true));
  });
});
