import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { LOGIN_ERROR_MESSAGE } from "../auth/useLogin";
import { fakeOidc, renderApp } from "../test/harness";
import { Landing } from "./Landing";

const enter = () => screen.findByRole("button", { name: "Entrar o crear cuenta" });

describe("Landing", () => {
  it("sends the player to Keycloak and shows that it is connecting while it waits", async () => {
    const oidc = fakeOidc(null);
    let release: () => void = () => undefined;
    oidc.spies.signinRedirect.mockImplementation(() => new Promise<void>((resolve) => (release = resolve)));
    renderApp(<Landing />, { oidc });

    await userEvent.click(await enter());

    const busy = await screen.findByRole("button", { name: "Conectando…" });
    expect(busy).toBeDisabled();
    expect(screen.getByText("Conectando con el servidor de identidad…")).toBeInTheDocument();
    expect(oidc.spies.signinRedirect).toHaveBeenCalledWith({ state: { returnTo: "/" } });
    await act(async () => release());
  });

  it("explains that the identity server is not available yet instead of doing nothing, and lets the player try again", async () => {
    const oidc = fakeOidc(null);
    oidc.spies.signinRedirect.mockRejectedValueOnce(new Error("Failed to fetch"));
    renderApp(<Landing />, { oidc });

    await userEvent.click(await enter());

    expect(await screen.findByRole("alert")).toHaveTextContent(LOGIN_ERROR_MESSAGE);
    expect(screen.getByRole("button", { name: "Entrar o crear cuenta" })).toBeEnabled(); // vuelve a estar disponible

    // Cuando Keycloak ya esta arriba, el segundo intento sigue y el aviso desaparece.
    await userEvent.click(screen.getByRole("button", { name: "Entrar o crear cuenta" }));
    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(oidc.spies.signinRedirect).toHaveBeenCalledTimes(2);
  });

  it("also tells the player when Keycloak does not answer at all", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const oidc = fakeOidc(null);
      oidc.spies.signinRedirect.mockImplementation(() => new Promise<void>(() => undefined)); // nunca responde
      renderApp(<Landing />, { oidc });

      await userEvent.click(await enter());
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_100);
      });

      expect(await screen.findByRole("alert")).toHaveTextContent(LOGIN_ERROR_MESSAGE);
      expect(screen.getByRole("button", { name: "Entrar o crear cuenta" })).toBeEnabled();
    } finally {
      vi.useRealTimers();
    }
  });
});
