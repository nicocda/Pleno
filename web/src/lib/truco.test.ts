import type { TrucoEvent } from "../api/types";
import { MANO_NAMES, actionLabel, bannerFor, byStrength, decodeCard, eventText, groupOf, newEvents, pendingText, pileSpot, playBody, strengthOf, trucoWorth } from "./truco";

const nameOf = (seat: number) => (seat === 0 ? "Jugador 1" : "Bot 2");
const ev = (e: Partial<TrucoEvent> & Pick<TrucoEvent, "seat" | "kind">): TrucoEvent => ({ card: null, value: null, a: null, b: null, ...e });

describe("truco cards", () => {
  it("decodes suit and number and builds an accessible label", () => {
    expect(decodeCard(0)).toMatchObject({ suit: 0, number: 1, label: "1 de espadas" });
    expect(decodeCard(16)).toMatchObject({ suit: 1, number: 7, label: "7 de bastos" });
    expect(decodeCard(27).label).toBe("10 de oros");
    expect(decodeCard(39).label).toBe("12 de copas");
    expect(() => decodeCard(40)).toThrow(RangeError);
  });

  it("ranks the cards like the server and sorts the best first", () => {
    const ace = 0, aceBastos = 10, sevenSwords = 6, sevenGold = 26, three = 2, two = 21, fakeAce = 30, four = 3, sevenCups = 36;
    expect([aceBastos, four, ace, sevenCups, three, fakeAce, two, sevenGold, sevenSwords].sort(byStrength)).toEqual([ace, aceBastos, sevenSwords, sevenGold, three, two, fakeAce, sevenCups, four]);
    expect(strengthOf(ace)).toBe(14);
    expect(strengthOf(four)).toBe(1);
  });
});

describe("truco event texts", () => {
  it("tells calls, answers and plays", () => {
    expect(eventText(ev({ seat: 1, kind: "truco", value: 2 }), nameOf)).toBe("Bot 2 cantó truco");
    expect(eventText(ev({ seat: 0, kind: "vale4", value: 4 }), nameOf)).toBe("Jugador 1 cantó vale cuatro");
    expect(eventText(ev({ seat: 0, kind: "falta_envido" }), nameOf)).toBe("Jugador 1 cantó falta envido");
    expect(eventText(ev({ seat: 1, kind: "no_quiero" }), nameOf)).toBe("Bot 2 no quiso");
    expect(eventText(ev({ seat: 0, kind: "quiero" }), nameOf)).toBe("Jugador 1 quiso");
    expect(eventText(ev({ seat: 0, kind: "mazo" }), nameOf)).toBe("Jugador 1 se fue al mazo");
    expect(eventText(ev({ seat: 0, kind: "play", card: 0 }), nameOf)).toBe("Jugador 1 jugó 1 de espadas");
    expect(eventText(ev({ seat: 0, kind: "start" }), nameOf)).toBe("Empezó la ronda");
  });

  it("tells manos, envido results and the end of a round", () => {
    expect(eventText(ev({ seat: 0, kind: "baza" }), nameOf)).toBe("Ganó la mano Jugador 1"); // el servidor la llama "baza"; en pantalla es "mano"
    expect(eventText(ev({ seat: -1, kind: "parda" }), nameOf)).toBe("Parda");
    expect(eventText(ev({ seat: 0, kind: "envido_result", value: 2, a: 33, b: 27 }), nameOf)).toBe("Envido: Jugador 1 tenía 33 y Bot 2 27 — ganó Jugador 1 (+2)");
    expect(eventText(ev({ seat: 1, kind: "hand_end", value: 2 }), nameOf)).toBe("Ronda para Bot 2: +2");
  });
});

describe("truco actions", () => {
  it("labels each move in Spanish and turns raises into answers when a truco is pending", () => {
    expect(actionLabel("envido", null)).toBe("Envido");
    expect(actionLabel("real_envido", null)).toBe("Real envido");
    expect(actionLabel("falta_envido", null)).toBe("Falta envido");
    expect(actionLabel("truco", null)).toBe("Truco");
    expect(actionLabel("retruco", null)).toBe("Retruco");
    expect(actionLabel("vale4", null)).toBe("Vale cuatro");
    expect(actionLabel("quiero", null)).toBe("Quiero");
    expect(actionLabel("no_quiero", null)).toBe("No quiero");
    expect(actionLabel("mazo", null)).toBe("Al mazo");
    const truco = { kind: "truco" as const, caller: 1, level: 1, calls: [] };
    expect(actionLabel("retruco", truco)).toBe("Quiero retruco");
    expect(actionLabel("vale4", { ...truco, level: 2 })).toBe("Quiero vale cuatro");
    expect(actionLabel("envido", truco)).toBe("Envido");
    expect(actionLabel("real_envido", { kind: "envido", caller: 1, level: 0, calls: ["envido"] })).toBe("Subir: real envido");
  });

  it("groups the moves and words the pending call", () => {
    expect(groupOf("real_envido")).toBe("envido");
    expect(groupOf("vale4")).toBe("truco");
    expect(groupOf("no_quiero")).toBe("response");
    expect(groupOf("mazo")).toBe("mazo");
    expect(pendingText({ kind: "truco", caller: 1, level: 1, calls: [] }, nameOf)).toBe("Bot 2 cantó truco: ¿quiero?");
    expect(pendingText({ kind: "envido", caller: 0, level: 0, calls: ["envido", "real_envido"] }, nameOf)).toBe("Jugador 1 cantó envido, real envido: ¿quiero?");
    expect(playBody(7)).toEqual({ type: "play", card: 7 });
    expect([0, 1, 2, 3].map(trucoWorth)).toEqual([1, 2, 3, 4]);
  });
});

describe("la pila de cartas de la mesa", () => {
  it("names the three manos of a round", () => {
    expect(MANO_NAMES).toEqual(["Primera mano", "Segunda mano", "Tercera mano"]);
  });

  it("stacks the cards two by two: the second one of a mano falls to the right and a bit lower, and every new mano starts higher than the previous one", () => {
    for (const mano of [0, 1, 2]) {
      const first = pileSpot(mano * 2);
      const second = pileSpot(mano * 2 + 1);

      expect(second.dx).toBeGreaterThan(first.dx);
      expect(second.dy).toBeGreaterThan(first.dy);
      expect(second.dx - first.dx).toBeLessThan(66); // sigue encimada: la carta mide 66 px
    }

    expect(pileSpot(2).dy).toBeLessThan(pileSpot(0).dy); // la 3ra arranca mas arriba que la 1ra
    expect(pileSpot(4).dy).toBeLessThan(pileSpot(2).dy); // y la 5ta vuelve a subir
    expect(pileSpot(3).dy).toBeLessThan(pileSpot(1).dy); // la 4ta cae abajo a la derecha de la 3ra, pero mas arriba que la 2da
  });

  it("throws the cards a little crooked, differently each one, and is deterministic", () => {
    const rotations = [0, 1, 2, 3, 4, 5].map((i) => pileSpot(i).rot);

    expect(new Set(rotations).size).toBeGreaterThan(3);
    expect(rotations.every((r) => Math.abs(r) <= 12)).toBe(true);
    expect(pileSpot(3)).toEqual(pileSpot(3));
  });
});

describe("carteleria", () => {
  const ev = (seat: number, kind: TrucoEvent["kind"], extra: Partial<TrucoEvent> = {}): TrucoEvent => ({ seat, kind, card: null, value: null, a: null, b: null, ...extra });
  const name = (seat: number) => (seat === 0 ? "Ana" : "Bot 2");

  it("finds the new events even when the server window slides", () => {
    const a = ev(0, "play", { card: 1 });
    const b = ev(1, "truco");
    const c = ev(0, "quiero");
    expect(newEvents([a], [a, b])).toEqual([b]);
    expect(newEvents([a, b], [b, c])).toEqual([c]); // la ventana se corrio: salio a, entro c
    expect(newEvents([a, b], [a, b])).toEqual([]);
    expect(newEvents([], [a, b])).toEqual([a, b]);
    expect(newEvents([a], [b, c])).toEqual([b, c]); // sin nada en comun: todo es nuevo
  });

  it("announces what the rival calls and answers from my point of view", () => {
    expect(bannerFor(ev(1, "truco"), 0, name)).toMatchObject({ tone: "call", title: "¡TRUCO!", detail: "Bot 2 te cantó" });
    expect(bannerFor(ev(0, "retruco"), 0, name)).toMatchObject({ title: "¡RETRUCO!", detail: "Cantaste vos" });
    expect(bannerFor(ev(1, "quiero"), 0, name)).toMatchObject({ title: "¡QUIERO!", detail: "Bot 2 aceptó" });
    expect(bannerFor(ev(1, "no_quiero"), 0, name)).toMatchObject({ tone: "good", title: "NO QUIERO" });
    expect(bannerFor(ev(0, "no_quiero"), 0, name)).toMatchObject({ tone: "bad" });
  });

  it("tells who won each mano, the envido and the round, and stays neutral for a spectator", () => {
    expect(bannerFor(ev(0, "baza"), 0, name)).toMatchObject({ tone: "good", title: "Ganaste la mano" });
    expect(bannerFor(ev(1, "baza"), 0, name)).toMatchObject({ tone: "bad", title: "Perdiste la mano" });
    expect(bannerFor(ev(-1, "parda"), 0, name)).toMatchObject({ tone: "info", title: "Parda" });
    expect(bannerFor(ev(1, "envido_result", { value: 2, a: 26, b: 28 }), 0, name)).toMatchObject({ tone: "bad", title: "Perdiste el envido", detail: "(+2) · 26 a 28" });
    expect(bannerFor(ev(0, "hand_end", { value: 3 }), 0, name)).toMatchObject({ tone: "good", title: "¡Ganaste la ronda!", detail: "+3 para vos" });
    expect(bannerFor(ev(1, "hand_end", { value: 1 }), 0, name)).toMatchObject({ tone: "bad", title: "Perdiste la ronda" });
    expect(bannerFor(ev(1, "hand_end", { value: 1 }), null, name)).toMatchObject({ tone: "info", title: "Ronda para Bot 2" });
    expect(bannerFor(ev(0, "play", { card: 3 }), 0, name)).toBeNull();
    expect(bannerFor(ev(0, "start"), 0, name)).toBeNull();
  });
});
