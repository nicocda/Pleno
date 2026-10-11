import type { CrashRound } from "../api/types";
import { crashPointOf, crashView, formatMultiplier, MAX_MULTIPLIER, multiplierAt, ONE_X, parseAutoCashOut, sha256Hex, timeToReach, verifyRound } from "./crash";

const SEED = "9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff";
const round = (n: number) => `00000000-0000-0000-0000-${n.toString(16).padStart(12, "0")}`;

// Los mismos vectores que las pruebas del servidor (C#): calculados con una implementacion independiente (Python).
const VECTORS: [number, number, number][] = [
  [1, 30, 346],
  [2, 30, 1198],
  [3, 30, 143],
  [4, 30, 114],
  [5, 30, 176],
  [6, 30, 238],
  [7, 30, 100],
  [8, 30, 395],
  [1, 0, 357],
  [2, 0, 1235],
  [1, 100, 321],
  [7, 100, 100],
];

const crashed = (overrides: Partial<CrashRound> = {}): CrashRound => ({
  id: round(1), phase: "Crashed", commitment: "", openedAt: "2026-10-09T12:00:00Z", bettingEndsAt: "2026-10-09T12:00:08Z",
  startedAt: "2026-10-09T12:00:08Z", crashedAt: "2026-10-09T12:00:12Z", crashPoint: 346, serverSeed: SEED, edgePermille: 30, growthPerSecond: 0.07, ...overrides,
});

describe("crash point verification", () => {
  it.each(VECTORS)("recomputes round %i with edge %i to %i, like the server", async (n, edge, expected) => {
    expect(await crashPointOf(SEED, round(n), edge)).toBe(expected);
  });

  it("matches the commitment published before betting", async () => {
    expect(await sha256Hex(SEED)).toBe("52ca56a3d81d3be381d594a5bca342bb63f6a3597776fed89138c9244fcbd2c8");
  });

  it("verifies a finished round: the commitment and the reported point both hold", async () => {
    const result = await verifyRound(crashed({ commitment: await sha256Hex(SEED) }));

    expect(result).toEqual({ commitmentOk: true, crashPointOk: true, computedCrashPoint: 346 });
  });

  it("catches a seed that does not match the commitment and a reported point that was not the real one", async () => {
    const wrongCommitment = await verifyRound(crashed({ commitment: "0".repeat(64) }));
    const wrongPoint = await verifyRound(crashed({ commitment: await sha256Hex(SEED), crashPoint: 500 }));

    expect(wrongCommitment?.commitmentOk).toBe(false);
    expect(wrongPoint).toMatchObject({ commitmentOk: true, crashPointOk: false, computedCrashPoint: 346 });
  });

  it("has nothing to verify until the seed is revealed", async () => {
    expect(await verifyRound(crashed({ serverSeed: null, crashPoint: null }))).toBeNull();
  });

  it("accepts an uppercase commitment", async () => {
    const result = await verifyRound(crashed({ commitment: (await sha256Hex(SEED)).toUpperCase() }));

    expect(result?.commitmentOk).toBe(true);
  });
});

describe("multiplier", () => {
  it("starts at x1, only grows and stops at the cap", () => {
    expect(multiplierAt(0, 0.07)).toBe(ONE_X);
    expect(multiplierAt(-3, 0.07)).toBe(ONE_X);
    let previous = ONE_X;
    for (let t = 0; t <= 130; t += 0.25) {
      const current = multiplierAt(t, 0.07);
      expect(current).toBeGreaterThanOrEqual(previous);
      previous = current;
    }
    expect(multiplierAt(200, 0.07)).toBe(MAX_MULTIPLIER);
    expect(multiplierAt(10, 0.07)).toBeGreaterThanOrEqual(195);
    expect(multiplierAt(10, 0.07)).toBeLessThanOrEqual(205);
  });

  it("gets to a multiplier exactly at timeToReach", () => {
    for (const m of [101, 150, 250, 1_000, 5_000]) {
      const t = timeToReach(m, 0.07);
      expect(multiplierAt(t + 0.002, 0.07)).toBeGreaterThanOrEqual(m);
      expect(multiplierAt(t - 0.002, 0.07)).toBeLessThan(m);
    }
    expect(timeToReach(ONE_X, 0.07)).toBe(0);
  });

  it("formats with two decimals in the local style", () => {
    expect(formatMultiplier(100)).toBe("1,00x");
    expect(formatMultiplier(250)).toBe("2,50x");
    expect(formatMultiplier(123_456)).toBe("1.234,56x");
  });
});

describe("parseAutoCashOut", () => {
  it("accepts a comma or a dot and from 1,01 to 100", () => {
    expect(parseAutoCashOut("2,5")).toBe(2.5);
    expect(parseAutoCashOut("2.5")).toBe(2.5);
    expect(parseAutoCashOut(" 1.01 ")).toBe(1.01);
    expect(parseAutoCashOut("100")).toBe(100);
  });

  it("refuses anything else", () => {
    for (const bad of ["", "abc", "1", "1.00", "0.5", "100.01", "101", "1000", "2.555", "-3", "1e3"]) expect(parseAutoCashOut(bad)).toBeNull();
  });
});

describe("crashView", () => {
  const base = Date.parse("2026-10-09T12:00:00Z");
  const betting = crashed({ phase: "Betting", startedAt: null, crashedAt: null, crashPoint: null, serverSeed: null, bettingEndsAt: "2026-10-09T12:00:08Z" });

  it("waits when there is no round yet", () => {
    expect(crashView(null, base).phase).toBe("waiting");
  });

  it("counts down the betting window and never goes below zero", () => {
    expect(crashView(betting, base).secondsLeft).toBe(8);
    expect(crashView(betting, base + 3_000)).toMatchObject({ phase: "betting", secondsLeft: 5, multiplier: ONE_X });
    expect(crashView(betting, base + 20_000).secondsLeft).toBe(0);
  });

  it("shows the multiplier rising from the moment the round started", () => {
    const running = crashed({ phase: "Running", crashedAt: null, crashPoint: null, serverSeed: null, startedAt: "2026-10-09T12:00:08Z", growthPerSecond: 0.07 });

    expect(crashView(running, base + 8_000)).toMatchObject({ phase: "running", multiplier: ONE_X, elapsed: 0 });
    expect(crashView(running, base + 18_000).multiplier).toBe(multiplierAt(10, 0.07));
    expect(crashView(running, base).elapsed).toBe(0); // un reloj levemente atrasado no da un tiempo negativo
  });

  it("freezes at the crash point once it exploded and shows nothing special when aborted", () => {
    expect(crashView(crashed({ crashPoint: 346 }), base + 99_000)).toMatchObject({ phase: "crashed", multiplier: 346 });
    expect(crashView(crashed({ phase: "Aborted", crashPoint: null }), base).phase).toBe("aborted");
  });
});
