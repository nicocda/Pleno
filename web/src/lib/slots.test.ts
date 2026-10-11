import { CASCADE_STEP_MS, cascadeFrames, explodingReels, winTier, winningReels } from "./slots";

describe("slots prizes", () => {
  it("sizes the celebration by how many times the stake came back", () => {
    expect(winTier(3)).toBe("win");
    expect(winTier(10)).toBe("big");
    expect(winTier(50)).toBe("mega");
    expect(winTier(null)).toBe("win");
  });

  it("lights up the reels that make the prize", () => {
    expect(winningReels(["Siete", "Siete", "Siete"])).toEqual([true, true, true]);
    expect(winningReels(["Cereza", "Cereza", "Bar"])).toEqual([true, true, false]);
    expect(winningReels(["Cereza", "Bar", "Cereza"])).toEqual([true, false, false]);
  });
});

describe("slots cascades", () => {
  const steps = [
    { reels: ["Siete", "Siete", "Bar"], pay: 0, multiplier: 1 },
  ];

  it("explodes only the reels that formed a prize that chains", () => {
    expect(explodingReels({ reels: ["Cereza", "Cereza", "Bar"], pay: 3, multiplier: 1 }, 2)).toEqual([true, true, false]);
    expect(explodingReels({ reels: ["Cereza", "Bar", "Bar"], pay: 1, multiplier: 1 }, 2)).toEqual([false, false, false]); // solo recupera la apuesta: no encadena
    expect(explodingReels(steps[0]!, 2)).toEqual([false, false, false]);
  });

  it("scripts each cascade as a burst followed by the new symbols falling with the higher multiplier", () => {
    const chain = [
      { reels: ["Cereza", "Cereza", "Bar"], pay: 3, multiplier: 1 },
      { reels: ["Cereza", "Cereza", "Bar"], pay: 3, multiplier: 2 },
      { reels: ["Limon", "Bar", "Bar"], pay: 0, multiplier: 3 },
    ];

    const { frames, totalMs } = cascadeFrames(chain, 2);

    expect(frames).toHaveLength(4); // dos cascadas, cada una explota y cae
    expect(frames[0]!.view).toMatchObject({ multiplier: 1, bursting: [true, true, false], step: 0 });
    expect(frames[1]!.view).toMatchObject({ multiplier: 2, dropping: [true, true, false], step: 1 });
    expect(frames[3]!.view).toMatchObject({ reels: ["Limon", "Bar", "Bar"], multiplier: 3, step: 2 });
    expect(frames.map((f) => f.at)).toEqual([...frames.map((f) => f.at)].sort((a, b) => a - b)); // en orden
    expect(totalMs).toBe(2 * CASCADE_STEP_MS);
  });

  it("has nothing to play when the spin did not chain", () => {
    expect(cascadeFrames(steps, 2)).toEqual({ frames: [], totalMs: 0 });
    expect(cascadeFrames([], 2)).toEqual({ frames: [], totalMs: 0 });
  });
});
