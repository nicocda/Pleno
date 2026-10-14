import { limitLabel, multiplierLabel, payoutFor, wins, formatHundredths } from "./dice";

describe("dice helpers", () => {
  it("pays stake x 9800 / chance rounded down, like the server", () => {
    expect(payoutFor(100, 5000)).toBe(196);
    expect(payoutFor(100, 200)).toBe(4900);
    expect(payoutFor(100, 9500)).toBe(103);
    expect(payoutFor(7, 3333)).toBe(20);
  });

  it("decides the win by direction and limit", () => {
    expect(wins("Under", 5000, 4999)).toBe(true);
    expect(wins("Under", 5000, 5000)).toBe(false);
    expect(wins("Over", 5000, 5000)).toBe(true);
    expect(wins("Over", 5000, 4999)).toBe(false);
  });

  it("formats hundredths, the multiplier and the limit", () => {
    expect(formatHundredths(4950)).toBe("49,50");
    expect(multiplierLabel(5000)).toBe("1,96");
    expect(limitLabel("Under", 4950)).toBe("menos de 49,50");
    expect(limitLabel("Over", 4950)).toBe("50,50 o más");
  });
});
