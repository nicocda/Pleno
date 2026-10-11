import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeApi, renderApp } from "../test/harness";
import { Layout } from "./Layout";

const menu = () => document.getElementById("menu-principal")!;

describe("Layout (barra superior)", () => {
  it("keeps the navigation, the theme toggle and the sign out in a menu that opens from a button", async () => {
    renderApp(<Layout />);
    const button = await screen.findByRole("button", { name: "Abrir el menú" });

    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(button).toHaveAttribute("aria-controls", "menu-principal");
    expect(menu()).not.toHaveClass("is-open");
    expect(menu()).toContainElement(screen.getByRole("navigation", { name: "Principal" }));
    expect(menu()).toContainElement(screen.getByRole("button", { name: "Salir" }));

    await userEvent.click(button);

    expect(screen.getByRole("button", { name: "Cerrar el menú" })).toHaveAttribute("aria-expanded", "true");
    expect(menu()).toHaveClass("is-open");
  });

  it("closes the menu with Escape, with a tap outside the bar and after going to another page", async () => {
    renderApp(<Layout />);
    const open = async () => userEvent.click(await screen.findByRole("button", { name: "Abrir el menú" }));

    await open();
    await userEvent.keyboard("{Escape}");
    expect(menu()).not.toHaveClass("is-open");

    await open();
    await userEvent.click(screen.getByRole("contentinfo")); // un toque fuera de la barra
    expect(menu()).not.toHaveClass("is-open");

    await open();
    await userEvent.click(screen.getByRole("link", { name: "Historial" }));
    expect(menu()).not.toHaveClass("is-open");
  });

  it("always shows the balance in the bar (outside the menu) so it is visible without opening anything", async () => {
    renderApp(<Layout />);

    const balance = (await screen.findAllByTestId("balance-value"))[0]!;

    expect(menu()).not.toContainElement(balance);
    expect(document.querySelector("header")).toContainElement(balance);
  });
  it("shows the Cajero link only to cashiers and heads of cashiers", async () => {
    const meOf = (roles: string[]) => ({ userId: "u1", displayName: "x", roles, accountId: "a", registeredAt: "2026-10-01T10:00:00Z" });
    const { unmount } = renderApp(<Layout />, { api: fakeApi({ "GET /me": () => meOf(["player", "cashier"]) }) });
    expect(await screen.findByRole("link", { name: "Cajero" })).toBeInTheDocument();
    unmount();

    renderApp(<Layout />, { api: fakeApi({ "GET /me": () => meOf(["player"]) }) });
    await screen.findByRole("link", { name: "Lobby" });
    expect(screen.queryByRole("link", { name: "Cajero" })).not.toBeInTheDocument();
  });
});
