import type { GameInfo } from "../api/types";
import { COMING_SOON, GAME_PAGES, lobbyGames, pageFor } from "./registry";

const info = (id: string): GameInfo => ({ id, name: id, tagline: "", route: `/${id}`, glyph: "?", resolution: "Server" });

describe("game registry", () => {
  it("has a page for the games that exist today, with unique ids and routes", () => {
    expect(pageFor("roulette")?.route).toBe("ruleta");
    expect(pageFor("slots")?.route).toBe("tragamonedas");
    expect(pageFor("crash")?.route).toBe("crash");
    expect(pageFor("blackjack")?.route).toBe("blackjack");
    expect(pageFor("uno")?.route).toBe("uno");
    expect(pageFor("poker")?.route).toBe("poker");
    expect(pageFor("truco")?.route).toBe("truco");
    expect(pageFor("no-existe")).toBeUndefined();
    expect(new Set(GAME_PAGES.map((p) => p.id)).size).toBe(GAME_PAGES.length);
    expect(new Set(GAME_PAGES.map((p) => p.route)).size).toBe(GAME_PAGES.length);
  });

  it("never lists as coming soon a game that already has a page", () => {
    for (const soon of COMING_SOON) expect(pageFor(soon.id)).toBeUndefined();
  });

  it("lists exactly the server catalog when every game has a server module", () => {
    expect(lobbyGames([info("roulette"), info("slots")]).map((g) => g.id)).toEqual(["roulette", "slots"]);
  });
});
