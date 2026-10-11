import { render } from "@testing-library/react";
import { SpanishCardFace, type SpanishSuit } from "./SpanishCard";

const SUITS: SpanishSuit[] = [0, 1, 2, 3];
const NUMBERS = [1, 2, 3, 4, 5, 6, 7, 10, 11, 12];

const faceOf = (suit: SpanishSuit, number: number) => render(<SpanishCardFace suit={suit} number={number} />).container.querySelector("svg")!;

describe("SpanishCardFace (baraja española)", () => {
  it.each(SUITS)("draws the right number of emblems for the numbers 1 to 7 in suit %i", (suit) => {
    for (const number of [1, 2, 3, 4, 5, 6, 7]) {
      expect(faceOf(suit, number).querySelectorAll("[data-pip]")).toHaveLength(number); // el 3 tiene tres espadas, el 7 siete oros...
    }
  });

  it("draws the sota, the caballo and the rey as figures with their name, and the emblem of the suit on them", () => {
    for (const [number, name] of [[10, "SOTA"], [11, "CABALLO"], [12, "REY"]] as const) {
      const svg = faceOf(2, number);

      expect(svg.querySelector(`[data-figure="${number}"]`)).not.toBeNull();
      expect(svg.textContent).toContain(name);
      expect(svg.querySelectorAll("[data-pip]")).toHaveLength(2); // el emblema del palo arriba y abajo del marco
    }
  });

  it("writes the number of the card in the corner, tints it by suit and stays decorative for screen readers", () => {
    const sword = faceOf(0, 7);
    const gold = faceOf(2, 7);

    expect(sword.querySelector("text")?.textContent).toBe("7");
    expect(sword.getAttribute("aria-hidden")).toBe("true");
    expect(sword.querySelector("text")?.getAttribute("fill")).not.toBe(gold.querySelector("text")?.getAttribute("fill"));
  });

  it("renders all forty cards of the deck without repeating a face", () => {
    const faces = new Set<string>();
    for (const suit of SUITS) for (const number of NUMBERS) faces.add(faceOf(suit, number).outerHTML);

    expect(faces.size).toBe(40);
  });
});
