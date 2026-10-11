import type { BlackjackRound, BlackjackSeat } from "../api/types";
import { cardFace, describeHand, handText, handValue, isBlackjack, isWin, resultText, shoeOf, verifyHand } from "./blackjack";
import { sha256Hex } from "./crash";

// Las cartas: A = 0, 2 = 1, ... 9 = 8, 10 = 9, J = 10, Q = 11, K = 12 (+13 por cada palo).
const A = 0, TWO = 1, FIVE = 4, SIX = 5, NINE = 8, TEN = 9, JACK = 10, KING = 12;

// Vectores de una implementacion independiente (Python), los mismos del servidor.
const VECTORS = [
  { seed: "blackjack-test-seed", id: "00000000-0000-0000-0000-000000000001", first: [0, 33, 7, 17, 6, 50, 45, 24, 22, 42, 40, 49], last: [42, 26, 23], checksum: 246087 },
  { seed: "9f2c0000000000000000000000000000000000000000000000000000000000ff", id: "0190a1b2-c3d4-7e5f-8091-a2b3c4d5e6f7", first: [44, 10, 1, 22, 35, 12, 0, 34, 18, 42, 4, 27], last: [19, 4, 11], checksum: 261287 },
];

describe("hand value", () => {
  it("counts figures as 10 and aces as 11 until that would bust", () => {
    expect(handValue([KING, FIVE])).toEqual({ total: 15, soft: false });
    expect(handValue([A, SIX])).toEqual({ total: 17, soft: true });
    expect(handValue([A, SIX, NINE])).toEqual({ total: 16, soft: false }); // el as baja a 1
    expect(handValue([A, A, NINE])).toEqual({ total: 21, soft: true });
    expect(handValue([KING, JACK, TWO])).toEqual({ total: 22, soft: false });
    expect(handValue([])).toEqual({ total: 0, soft: false });
  });

  it("only a two card 21 is a blackjack", () => {
    expect(isBlackjack([A, KING])).toBe(true);
    expect(isBlackjack([KING + 13, A + 26])).toBe(true);
    expect(isBlackjack([SIX, FIVE, TEN])).toBe(false); // 21 con tres cartas
    expect(isBlackjack([KING, TEN])).toBe(false);
  });

  it("describes a hand for the table", () => {
    expect(describeHand([])).toBe("");
    expect(describeHand([A, KING])).toBe("Blackjack");
    expect(describeHand([A, SIX])).toBe("17 blando");
    expect(describeHand([KING, SIX])).toBe("16");
    expect(describeHand([A, FIVE, FIVE])).toBe("21");
  });
});

describe("cards and texts", () => {
  it("names each card by rank and suit, red for hearts and diamonds", () => {
    expect(cardFace(0)).toEqual({ rank: "A", symbol: "♠", red: false, label: "As de picas" });
    expect(cardFace(13 + 12)).toMatchObject({ rank: "K", symbol: "♥", red: true, label: "K de corazones" });
    expect(cardFace(26 + 9)).toMatchObject({ rank: "10", symbol: "♦", red: true });
    expect(cardFace(39 + 10)).toMatchObject({ rank: "J", symbol: "♣", red: false, label: "J de tréboles" });
  });

  it("explains every result in Spanish", () => {
    expect(resultText("Blackjack")).toMatch(/3 a 2/);
    expect(resultText("Win")).toMatch(/1 a 1/);
    expect(resultText("Push")).toMatch(/devuelven la apuesta/);
    expect(resultText("Lose")).toMatch(/Perdiste/);
    expect(resultText("Bust")).toBe("Te pasaste");
    expect(handText("Stood")).toBe("Plantado");
    expect(isWin("Win")).toBe(true);
    expect(isWin("Blackjack")).toBe(true);
    expect(isWin("Push")).toBe(false);
    expect(isWin(null)).toBe(false);
  });
});

describe("shoe", () => {
  it.each(VECTORS)("recomputes the shoe of $id like the server", async ({ seed, id, first, last, checksum }) => {
    const shoe = await shoeOf(seed, id);

    expect(shoe).toHaveLength(312);
    expect(shoe.slice(0, 12)).toEqual(first);
    expect(shoe.slice(-3)).toEqual(last);
    expect(shoe.reduce((sum, card, i) => sum + i * card, 0) % 1_000_003).toBe(checksum);
  });

  it("has six of each card", async () => {
    const shoe = await shoeOf(VECTORS[0]!.seed, VECTORS[0]!.id);
    for (let card = 0; card < 52; card += 1) expect(shoe.filter((c) => c === card)).toHaveLength(6);
  });
});

describe("hand verification", () => {
  const { seed, id } = VECTORS[0]!;

  // Una mano de 2 asientos armada con el zapato real.
  const build = async (tweak: (round: BlackjackRound, seats: BlackjackSeat[]) => void = () => undefined) => {
    const shoe = await shoeOf(seed, id);
    const n = 2;
    const round: BlackjackRound = {
      id, tableId: "t1", phase: "Finished", commitment: await sha256Hex(seed), openedAt: "2026-10-09T12:00:00Z", bettingEndsAt: null, finishedAt: "2026-10-09T12:00:30Z",
      seatCount: n, dealer: { cards: [shoe[n]!, shoe[2 * n + 1]!], hiddenCards: 0 }, activeSeat: null, turnEndsAt: null, serverSeed: seed,
    };
    const seats: BlackjackSeat[] = [0, 1].map((s) => ({
      seat: s + 1, stake: 10, cards: [shoe[s]!, shoe[n + 1 + s]!], hand: "Stood", result: "Lose", payout: 0, status: "Settled", mine: s === 0, betId: null,
    }));
    tweak(round, seats);
    return { round, seats };
  };

  it("passes when the commitment matches and the dealt cards come from the shoe", async () => {
    const { round, seats } = await build();
    expect(await verifyHand(round, seats)).toEqual({ commitmentOk: true, dealtOk: true });
  });

  it("accepts extra cards drawn after the deal", async () => {
    const { round, seats } = await build((r, s) => {
      s[0]!.cards = [...s[0]!.cards, 5];
      r.dealer.cards = [...r.dealer.cards, 7];
    });
    expect((await verifyHand(round, seats))?.dealtOk).toBe(true);
  });

  it("catches a wrong commitment, a changed player card and a changed dealer card", async () => {
    const commitment = await build((r) => { r.commitment = "0".repeat(64); });
    expect((await verifyHand(commitment.round, commitment.seats))?.commitmentOk).toBe(false);
    const player = await build((_, s) => { s[1]!.cards = [s[1]!.cards[0]!, (s[1]!.cards[1]! + 1) % 52]; });
    expect(await verifyHand(player.round, player.seats)).toEqual({ commitmentOk: true, dealtOk: false });
    const dealer = await build((r) => { r.dealer.cards = [(r.dealer.cards[0]! + 1) % 52, r.dealer.cards[1]!]; });
    expect((await verifyHand(dealer.round, dealer.seats))?.dealtOk).toBe(false);
  });

  it("has nothing to verify until the seed is revealed", async () => {
    const { round, seats } = await build((r) => { r.serverSeed = null; });
    expect(await verifyHand(round, seats)).toBeNull();
  });
});
