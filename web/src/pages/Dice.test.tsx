import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { DiceRound, PlacedBet } from "../api/types";
import { fakeApi, renderApp, type FakeApi } from "../test/harness";
import { Dice } from "./Dice";

const account = (available: number) => ({ accountId: "a1", userId: "u1", available, reserved: 0, version: 2, openReservations: {} });
const placed = (betId: string): PlacedBet => ({ betId, status: "Placed", nonce: 0, commitment: "c".repeat(64), clientSeed: "abc", alreadyPlaced: false });
const settled = (roll: number, payout: number): DiceRound => ({
  betId: "bet-1", status: "Settled", direction: "Under", chance: 4950, stake: 10, pairId: "p", nonce: 0, roll, payout, failureReason: null, placedAt: "2026-10-14T12:00:00Z",
});

function apiWith(extra: Record<string, (body?: unknown, headers?: Record<string, string>) => unknown> = {}, available = 1000): FakeApi {
  return fakeApi({
    "GET /wallet/me": () => account(available),
    "GET /games/dice/rounds": () => [],
    "POST /games/dice/bets": () => placed("bet-1"),
    ...extra,
  });
}

const betCalls = (api: FakeApi) => api.calls.filter((c) => c.method === "POST" && c.path === "/games/dice/bets");

function reduceMotion() {
  window.matchMedia = ((query: string) => ({
    matches: query.includes("prefers-reduced-motion"), media: query,
    addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, onchange: null, dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}

describe("Dice", () => {
  beforeEach(() => reduceMotion());

  it("shows the multiplier and the potential payout for the chosen chance", async () => {
    renderApp(<Dice />, { api: apiWith() });

    expect(await screen.findByText(/Paga/)).toHaveTextContent("x1,97");
    expect(screen.getByText(/Si ganás, cobrás/)).toHaveTextContent("19");
  });

  it("places the bet with direction, chance, stake and an idempotency key", async () => {
    const api = apiWith({ "GET /games/dice/rounds/bet-1": () => settled(1234, 19) });
    renderApp(<Dice />, { api });

    await userEvent.click(await screen.findByRole("radio", { name: "Por encima" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Tirar por 10 fichas/ })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: /Tirar por 10 fichas/ }));

    await waitFor(() => expect(betCalls(api)).toHaveLength(1));
    expect(betCalls(api)[0]?.body).toEqual({ direction: "Over", chance: 4950, stake: 10 });
    expect(betCalls(api)[0]?.headers?.["Idempotency-Key"]).toBeTruthy();
  });

  it("reveals the rolled number when the polled round closes", async () => {
    const api = apiWith({ "GET /games/dice/rounds/bet-1": () => settled(1234, 0) });
    renderApp(<Dice />, { api });

    await waitFor(() => expect(screen.getByRole("button", { name: /Tirar por 10 fichas/ })).toBeEnabled());
    await act(async () => {
      await userEvent.click(screen.getByRole("button", { name: /Tirar por 10 fichas/ }));
    });

    expect(await screen.findByText("No hubo suerte esta vez")).toBeInTheDocument();
    expect(screen.getByTestId("dice-number")).toHaveTextContent("12,34");
  });
});
