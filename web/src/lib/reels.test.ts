import { DEFAULT_REEL_TIMINGS, ReelsModel } from "./reels";

/** Avanza el modelo en cuadros de 16 ms hasta que se asiente (o se agote el limite). */
function run(model: ReelsModel, limitMs = 20_000, frame = 16) {
  let elapsed = 0;
  while (model.phase !== "settled" && elapsed < limitMs) {
    model.update(frame);
    elapsed += frame;
  }
  return elapsed;
}

describe("ReelsModel", () => {
  it("starts idle with every reel resting", () => {
    const model = new ReelsModel(6);

    expect(model.phase).toBe("idle");
    expect(model.stopped.every(Boolean)).toBe(true);
    expect(model.result).toBeNull();
  });

  it("keeps spinning while the result is not known yet (the server may take a while)", () => {
    const model = new ReelsModel(6);
    model.spin();

    run(model, 10_000);

    expect(model.phase).toBe("spinning");
    expect(model.stopped.some(Boolean)).toBe(false);
  });

  it("changes the symbol of a spinning reel", () => {
    const model = new ReelsModel(6);
    model.spin();
    const seen = new Set<number>();

    for (let i = 0; i < 40; i++) {
      model.update(DEFAULT_REEL_TIMINGS.cycleMs);
      seen.add(model.display[0]!);
    }

    expect(seen.size).toBeGreaterThan(2);
  });

  it("stops every reel exactly on the symbol the server decided, for every combination", () => {
    for (let a = 0; a < 4; a++) {
      for (let b = 0; b < 4; b++) {
        for (let c = 0; c < 4; c++) {
          const model = new ReelsModel(4);
          model.spin();
          model.update(300);
          model.land([a, b, c]);
          run(model);

          expect(model.phase).toBe("settled");
          expect([...model.display]).toEqual([a, b, c]);
          expect(model.result).toEqual([a, b, c]);
        }
      }
    }
  });

  it("stops the reels one after the other, from left to right", () => {
    const model = new ReelsModel(6);
    model.spin();
    model.land([1, 2, 3]);
    const stopTimes: number[] = [];
    let elapsed = 0;

    while (model.phase !== "settled" && elapsed < 20_000) {
      const before = [...model.stopped];
      model.update(10);
      elapsed += 10;
      model.stopped.forEach((s, i) => {
        if (s && !before[i]) stopTimes[i] = elapsed;
      });
    }

    expect(stopTimes[0]!).toBeGreaterThanOrEqual(DEFAULT_REEL_TIMINGS.minSpinMs - 10);
    expect(stopTimes[1]! - stopTimes[0]!).toBeGreaterThanOrEqual(DEFAULT_REEL_TIMINGS.staggerMs - 20);
    expect(stopTimes[2]! - stopTimes[1]!).toBeGreaterThanOrEqual(DEFAULT_REEL_TIMINGS.staggerMs - 20);
  });

  it("does not stop before the minimum time even if the result is known at once", () => {
    const model = new ReelsModel(6);
    model.spin();
    model.land([0, 1, 2]);

    model.update(DEFAULT_REEL_TIMINGS.minSpinMs - 100);

    expect(model.stopped.some(Boolean)).toBe(false);
    expect(model.phase).toBe("spinning");
  });

  it("can stop on a result that arrives long after the minimum time", () => {
    const model = new ReelsModel(6);
    model.spin();
    model.update(DEFAULT_REEL_TIMINGS.minSpinMs + 5_000);
    expect(model.phase).toBe("spinning");

    model.land([5, 5, 5]);
    run(model);

    expect([...model.display]).toEqual([5, 5, 5]);
  });

  it("with reduced motion settles at once, with no animation", () => {
    const model = new ReelsModel(6, 3, { reduceMotion: true });
    model.spin();
    model.land([2, 4, 1]);

    expect(model.phase).toBe("settled");
    expect([...model.display]).toEqual([2, 4, 1]);
  });

  it("returns to rest when the spin is cancelled", () => {
    const model = new ReelsModel(6);
    model.spin();
    model.update(500);

    model.cancel();

    expect(model.phase).toBe("idle");
    expect(model.stopped.every(Boolean)).toBe(true);
    expect(model.result).toBeNull();
  });

  it("can spin again after settling", () => {
    const model = new ReelsModel(6);
    model.spin();
    model.land([1, 1, 1]);
    run(model);

    model.spin();
    expect(model.phase).toBe("spinning");
    expect(model.result).toBeNull();
    model.land([3, 0, 2]);
    run(model);

    expect([...model.display]).toEqual([3, 0, 2]);
  });

  it("lands correctly regardless of how the frames are sliced", () => {
    for (const frame of [4, 16, 33, 250]) {
      const model = new ReelsModel(6);
      model.spin();
      model.land([4, 2, 0]);
      run(model, 30_000, frame);

      expect([...model.display]).toEqual([4, 2, 0]);
    }
  });

  it("refuses a malformed result", () => {
    const model = new ReelsModel(6);
    model.spin();

    expect(() => model.land([1, 2])).toThrow(RangeError);
    expect(() => model.land([1, 2, 6])).toThrow(RangeError);
    expect(() => model.land([1, -1, 2])).toThrow(RangeError);
    expect(() => new ReelsModel(1)).toThrow(RangeError);
  });
});

describe("ReelsModel near miss", () => {
  const run = (result: number[], untilMs: number) => {
    const model = new ReelsModel(6, 3);
    model.spin();
    model.land(result);
    const step = 50;
    for (let t = 0; t < untilMs; t += step) model.update(step);
    return model;
  };

  it("holds the last reel back, with suspense, when the first two came out the same", () => {
    const tense = run([2, 2, 4], 2_000); // sin suspenso ya habrian frenado (1100 + 2 * 450 = 2000 ms)

    expect(tense.stopped).toEqual([true, true, false]);
    expect(tense.anticipating).toBe(true);

    const done = run([2, 2, 4], 3_200);
    expect(done.phase).toBe("settled");
    expect(done.anticipating).toBe(false);
  });

  it("does not add suspense when the first two differ", () => {
    const plain = run([2, 3, 4], 2_100);

    expect(plain.phase).toBe("settled");
    expect(plain.anticipating).toBe(false);
  });
});
