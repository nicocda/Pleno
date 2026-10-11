import type { PokerEvent, PokerShowdownEntry } from "../api/types";
import { categoryName, clampRaise, decodeCard, eventText, handName, isValidCard, raiseSize } from "./poker";

const names = (seat: number) => (seat === 2 ? "Bot 3" : `Jugador ${seat + 1}`);
const ev = (overrides: Partial<PokerEvent>): PokerEvent => ({ seat: 1, kind: "check", amount: null, allIn: false, ...overrides });

describe("poker cards", () => {
  it("decodes suit and rank: 0 is the 2 of spades, 12 the ace of spades, 13 the 2 of hearts", () => {
    expect(decodeCard(0)).toMatchObject({ suit: 0, rank: 2, rankSymbol: "2", symbol: "♠", red: false, label: "2 de picas" });
    expect(decodeCard(12)).toMatchObject({ rank: 14, rankSymbol: "A", label: "As de picas" });
    expect(decodeCard(13 + 8)).toMatchObject({ suit: 1, rank: 10, rankSymbol: "10", symbol: "♥", red: true, label: "10 de corazones" });
    expect(decodeCard(26 + 9)).toMatchObject({ suit: 2, rankSymbol: "J", symbol: "♦", red: true, label: "Jota de diamantes" });
    expect(decodeCard(39 + 10)).toMatchObject({ suit: 3, rankSymbol: "Q", symbol: "♣", red: false, label: "Reina de tréboles" });
    expect(decodeCard(39 + 11).label).toBe("Rey de tréboles");
  });

  it("rejects ids outside 0 to 51", () => {
    expect(isValidCard(51)).toBe(true);
    expect(isValidCard(52)).toBe(false);
    expect(isValidCard(-1)).toBe(false);
    expect(() => decodeCard(52)).toThrow(RangeError);
  });
});

describe("poker categories", () => {
  it("names every category in Spanish", () => {
    expect(["HighCard", "Pair", "TwoPair", "ThreeOfAKind", "Straight", "Flush", "FullHouse", "FourOfAKind", "StraightFlush"].map((c) => categoryName(c))).toEqual([
      "Carta alta", "Pareja", "Doble pareja", "Trío", "Escalera", "Color", "Full", "Póker", "Escalera de color",
    ]);
  });

  it("calls a straight flush with the ace on top a royal one", () => {
    const royal = [8, 9, 10, 11, 12, 30, 40]; // 10 a As de picas
    const lower = [0, 1, 2, 3, 4, 30, 40]; // 2 a 6 de picas
    expect(categoryName("StraightFlush", royal)).toBe("Escalera real");
    expect(categoryName("StraightFlush", lower)).toBe("Escalera de color");
    const entry: PokerShowdownEntry = { seat: 0, cards: [8, 9], category: "StraightFlush", won: true };
    expect(handName(entry, [10, 11, 12, 30, 40])).toBe("Escalera real");
  });
});

describe("poker events", () => {
  it("writes the facts of the hand in Spanish", () => {
    expect(eventText(ev({ seat: 1, kind: "raise", amount: 40 }), names)).toBe("Jugador 2 subió a 40");
    expect(eventText(ev({ seat: 2, kind: "call", amount: 20 }), names)).toBe("Bot 3 igualó 20");
    expect(eventText(ev({ seat: 0, kind: "check" }), names)).toBe("Jugador 1 pasó");
    expect(eventText(ev({ seat: 2, kind: "fold" }), names)).toBe("Se retiró Bot 3");
    expect(eventText(ev({ seat: 0, kind: "bet", amount: 30 }), names)).toBe("Jugador 1 apostó 30");
    expect(eventText(ev({ seat: 0, kind: "blind", amount: 1 }), names)).toBe("Jugador 1 puso la ciega de 1");
    expect(eventText(ev({ seat: 1, kind: "raise", amount: 1_000, allIn: true }), names)).toBe("Jugador 2 subió a 1.000 (all-in)");
  });

  it("names the cards of each street", () => {
    const board = [12, 21, 35, 0, 51];
    expect(eventText(ev({ seat: -1, kind: "flop" }), names, board)).toBe("Flop: As de picas, 10 de corazones, Jota de diamantes");
    expect(eventText(ev({ seat: -1, kind: "turn" }), names, board)).toBe("Turn: 2 de picas");
    expect(eventText(ev({ seat: -1, kind: "river" }), names, board)).toBe("River: As de tréboles");
    expect(eventText(ev({ seat: -1, kind: "flop" }), names)).toBe("Flop");
  });

  it("says who won, with the hand when there was a showdown", () => {
    const showdown: PokerShowdownEntry[] = [{ seat: 0, cards: [0, 1], category: "TwoPair", won: true }];
    expect(eventText(ev({ seat: 0, kind: "win_showdown", amount: 120 }), names, [], showdown)).toBe("Jugador 1 se llevó 120 con Doble pareja");
    expect(eventText(ev({ seat: 0, kind: "win", amount: 120 }), names)).toBe("Jugador 1 se llevó 120");
  });
});

describe("poker raise sizes", () => {
  const base = { pot: 100, myBet: 0, toCall: 20, minRaiseTo: 40, maxRaiseTo: 500 };

  it("clamps to the legal range and to whole numbers", () => {
    expect(clampRaise(10, 40, 500)).toBe(40);
    expect(clampRaise(900, 40, 500)).toBe(500);
    expect(clampRaise(77.9, 40, 500)).toBe(77);
    expect(clampRaise(Number.NaN, 40, 500)).toBe(40);
    expect(clampRaise(5, 40, 30)).toBe(30); // all-in por menos que el minimo
  });

  it("computes half pot, pot and all-in as the total to raise to", () => {
    expect(raiseSize("half", base)).toBe(80); // 0 + 20 + (100 + 20) / 2
    expect(raiseSize("pot", base)).toBe(140); // 0 + 20 + 120
    expect(raiseSize("allin", base)).toBe(500);
  });

  it("never leaves [minRaiseTo, maxRaiseTo]", () => {
    expect(raiseSize("half", { ...base, pot: 0, toCall: 0, minRaiseTo: 40 })).toBe(40);
    expect(raiseSize("pot", { ...base, maxRaiseTo: 100 })).toBe(100);
  });
});
