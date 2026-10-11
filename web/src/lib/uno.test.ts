import type { UnoEvent } from "../api/types";
import { colorOf, decodeCard, eventText, isWild, playBody, textOn } from "./uno";

const names = (seat: number) => (seat === 2 ? "Bot 3" : `Jugador ${seat + 1}`);
const ev = (overrides: Partial<UnoEvent>): UnoEvent => ({ seat: 1, kind: "play", card: null, color: null, count: null, ...overrides });

describe("uno cards", () => {
  it("decodes the four colors and the numbers of a color", () => {
    expect(decodeCard(0)).toMatchObject({ kind: "number", color: 0, number: 0, label: "0 rojo" });
    expect(decodeCard(5)).toMatchObject({ kind: "number", color: 0, number: 5, label: "5 rojo", symbol: "5" });
    expect(decodeCard(14)).toMatchObject({ number: 5, color: 0 }); // el segundo juego de 1 a 9
    expect(decodeCard(25 + 9)).toMatchObject({ number: 9, color: 1, label: "9 amarillo" });
    expect(decodeCard(50 + 3).label).toBe("3 verde");
    expect(decodeCard(75 + 18).label).toBe("9 azul");
  });

  it("decodes skip, reverse and draw two of each color", () => {
    expect(decodeCard(19)).toMatchObject({ kind: "skip", symbol: "⊘", label: "Salto rojo" });
    expect(decodeCard(20).kind).toBe("skip");
    expect(decodeCard(25 + 21)).toMatchObject({ kind: "reverse", symbol: "⇄", label: "Reversa amarillo" });
    expect(decodeCard(50 + 23)).toMatchObject({ kind: "draw2", symbol: "+2", label: "+2 verde" });
    expect(decodeCard(75 + 24).label).toBe("+2 azul");
  });

  it("decodes the wild cards, which have no color", () => {
    expect(decodeCard(100)).toMatchObject({ kind: "wild", color: null, label: "Comodín" });
    expect(decodeCard(103).kind).toBe("wild");
    expect(decodeCard(104)).toMatchObject({ kind: "wild4", color: null, label: "Comodín +4", symbol: "+4" });
    expect(decodeCard(107).kind).toBe("wild4");
    expect(isWild(99)).toBe(false);
    expect(isWild(100)).toBe(true);
    expect(colorOf(107)).toBeNull();
  });

  it("decodes the 108 cards of the deck and rejects ids out of range", () => {
    const all = Array.from({ length: 108 }, (_, i) => decodeCard(i));
    expect(all.filter((c) => c.kind === "wild")).toHaveLength(4);
    expect(all.filter((c) => c.kind === "wild4")).toHaveLength(4);
    expect(all.filter((c) => c.kind === "number")).toHaveLength(76);
    expect(() => decodeCard(108)).toThrow(RangeError);
    expect(() => decodeCard(-1)).toThrow(RangeError);
  });

  it("uses dark text on yellow only", () => {
    expect(textOn(1)).toBe("#1b1b1b");
    expect(textOn(0)).toBe("#ffffff");
    expect(textOn(null)).toBe("#ffffff");
  });

  it("builds the play body, with the chosen color only for wild cards", () => {
    expect(playBody(5)).toEqual({ type: "play", card: 5 });
    expect(playBody(105, 3)).toEqual({ type: "play", card: 105, color: 3 });
  });
});

describe("uno events", () => {
  it("tells who played what", () => {
    expect(eventText(ev({ card: 73, color: 2 }), names)).toBe("Jugador 2 jugó un +2 verde");
    expect(eventText(ev({ seat: 2, card: 5, color: 0 }), names)).toBe("Bot 3 jugó un 5 rojo");
  });

  it("says the color chosen with a wild card", () => {
    expect(eventText(ev({ card: 104, color: 3 }), names)).toBe("Jugador 2 jugó un comodín +4 y eligió azul");
    expect(eventText(ev({ card: 101, color: 1 }), names)).toBe("Jugador 2 jugó un comodín y eligió amarillo");
  });

  it("tells draws, passes, skips and penalties", () => {
    expect(eventText(ev({ seat: 2, kind: "draw", count: 1 }), names)).toBe("Bot 3 robó una carta");
    expect(eventText(ev({ kind: "draw", count: 0 }), names)).toBe("Jugador 2 quiso robar pero no quedan cartas");
    expect(eventText(ev({ kind: "pass" }), names)).toBe("Jugador 2 pasó");
    expect(eventText(ev({ kind: "skipped" }), names)).toBe("Jugador 2 se salteó");
    expect(eventText(ev({ seat: 3, kind: "penalty", count: 2 }), names)).toBe("Jugador 4 levantó 2 cartas");
    expect(eventText(ev({ kind: "penalty", count: 1 }), names)).toBe("Jugador 2 levantó una carta");
  });

  it("tells how the game started", () => {
    expect(eventText(ev({ seat: 0, kind: "start", card: 12, color: 0 }), names)).toBe("Empezó la partida con un 3 rojo");
  });
});
