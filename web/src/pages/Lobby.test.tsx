import { screen, within } from "@testing-library/react";
import type { GameInfo } from "../api/types";
import { fakeApi, renderApp } from "../test/harness";
import { Lobby } from "./Lobby";

const account = { accountId: "a1", userId: "u1", available: 1_000, reserved: 0, version: 1, openReservations: {} };
const me = { userId: "u1", displayName: "nicocda", roles: ["player"], accountId: "a1", registeredAt: "2026-10-01T10:00:00Z" };

const roulette: GameInfo = { id: "roulette", name: "Ruleta europea", tagline: "37 casilleros.", route: "/ruleta", glyph: "◎", resolution: "Server" };
const slots: GameInfo = { id: "slots", name: "Tragamonedas", tagline: "3 rodillos.", route: "/tragamonedas", glyph: "♣", resolution: "Server" };

const apiWith = (catalog: () => GameInfo[]) =>
  fakeApi({
    "GET /me": () => me,
    "GET /wallet/me": () => account,
    "GET /games": catalog,
    "GET /games/roulette/rounds": () => [],
  });

const section = () => within(screen.getByRole("region", { name: "Juegos" }));

describe("Lobby catalog", () => {
  it("builds the cards from the server catalog and links each one to its page", async () => {
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots]) });

    const link = await section().findByRole("link", { name: /Ruleta europea/ });
    expect(link).toHaveAttribute("href", "/ruleta");
    expect(section().getByRole("link", { name: /Tragamonedas/ })).toHaveAttribute("href", "/tragamonedas");
  });

  it("does not show a game the server no longer has enabled (removing a game does not touch the front)", async () => {
    renderApp(<Lobby />, { api: apiWith(() => [roulette]) });

    await section().findByRole("link", { name: /Ruleta europea/ });
    expect(section().queryByText("Tragamonedas")).not.toBeInTheDocument();
  });

  it("shows a game the server has but this front cannot play yet as coming soon, not as a broken link", async () => {
    const dice: GameInfo = { id: "dados", name: "Dados", tagline: "Dos dados.", route: "/dados", glyph: "⚀", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, dice]) });

    await section().findByRole("link", { name: /Ruleta europea/ });
    expect(section().queryByRole("link", { name: /Dados/ })).not.toBeInTheDocument();
    expect(section().getAllByText("Dados")).toHaveLength(1);
  });

  it("lists Crash with a link now that the front has its page", async () => {
    const crash: GameInfo = { id: "crash", name: "Crash", tagline: "Un cohete.", route: "/crash", glyph: "▲", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots, crash]) });

    expect(await section().findByRole("link", { name: /Crash/ })).toHaveAttribute("href", "/crash");
    expect(section().getAllByText("Crash")).toHaveLength(1);
  });

  it("lists Blackjack with a link now that the front has its page", async () => {
    const blackjack: GameInfo = { id: "blackjack", name: "Blackjack", tagline: "Mesas contra el crupier.", route: "/blackjack", glyph: "♠", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots, blackjack]) });

    expect(await section().findByRole("link", { name: /Blackjack/ })).toHaveAttribute("href", "/blackjack");
    expect(section().getAllByText("Blackjack")).toHaveLength(1);
  });

  it("lists Uno with a link now that the front has its page", async () => {
    const uno: GameInfo = { id: "uno", name: "Uno", tagline: "Mesas entre jugadores.", route: "/uno", glyph: "U", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots, uno]) });

    expect(await section().findByRole("link", { name: /Uno/ })).toHaveAttribute("href", "/uno");
    expect(section().getAllByText("Uno")).toHaveLength(1);
  });

  it("lists Poker with a link now that the front has its page", async () => {
    const poker: GameInfo = { id: "poker", name: "Poker", tagline: "Texas Hold'em entre jugadores.", route: "/poker", glyph: "P", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots, poker]) });

    expect(await section().findByRole("link", { name: /Poker/ })).toHaveAttribute("href", "/poker");
    expect(section().getAllByText("Poker")).toHaveLength(1);
  });

  it("lists Truco with a link now that the front has its page", async () => {
    const truco: GameInfo = { id: "truco", name: "Truco", tagline: "Mesas entre jugadores.", route: "/truco", glyph: "T", resolution: "Server" };
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots, truco]) });

    expect(await section().findByRole("link", { name: /Truco/ })).toHaveAttribute("href", "/truco");
    expect(section().getAllByText("Truco")).toHaveLength(1);
  });


  it("does not show as coming soon a game the server does not have and the front already has a page for", async () => {
    renderApp(<Lobby />, { api: apiWith(() => [roulette, slots]) });

    await section().findByRole("link", { name: /Ruleta europea/ });
    expect(section().queryByText("Poker")).not.toBeInTheDocument();
    expect(section().queryByText("Blackjack")).not.toBeInTheDocument();
  });

  it("tells the player when the catalog cannot be loaded", async () => {
    const api = fakeApi({ "GET /me": () => me, "GET /wallet/me": () => account, "GET /games/roulette/rounds": () => [] });
    renderApp(<Lobby />, { api });

    expect(await screen.findByText("No pudimos cargar los juegos.")).toBeInTheDocument();
  });
});
