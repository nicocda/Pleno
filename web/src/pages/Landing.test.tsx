import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AuthError } from "../auth/identity";
import { authMessage, UNREACHABLE_MESSAGE } from "../auth/messages";
import { fakeAuth, renderApp } from "../test/harness";
import { Landing } from "./Landing";

const loginForm = async (login: string, password: string) => {
  await userEvent.type(await screen.findByLabelText("Alias o email"), login);
  await userEvent.type(screen.getByLabelText("Contraseña"), password);
};

describe("Landing", () => {
  it("signs in with the alias and the password", async () => {
    const auth = fakeAuth(null);
    renderApp(<Landing />, { auth });

    await loginForm("jugador1", "clave-larga-123");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    await waitFor(() => expect(auth.spies.signIn).toHaveBeenCalledWith("jugador1", "clave-larga-123"));
  });

  it("shows a clear message for wrong credentials and lets the player try again", async () => {
    const auth = fakeAuth(null);
    auth.spies.signIn.mockRejectedValueOnce(new AuthError("invalid_credentials"));
    renderApp(<Landing />, { auth });

    await loginForm("jugador1", "mala");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(authMessage("invalid_credentials"));
    expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
  });

  it("explains that the identity server is not available yet instead of doing nothing", async () => {
    const auth = fakeAuth(null);
    auth.spies.signIn.mockRejectedValueOnce(new AuthError("unreachable"));
    renderApp(<Landing />, { auth });

    await loginForm("jugador1", "clave-larga-123");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(UNREACHABLE_MESSAGE);
  });

  it("offers to resend the verification email when the account is not verified", async () => {
    const auth = fakeAuth(null);
    auth.spies.signIn.mockRejectedValueOnce(new AuthError("email_not_verified"));
    renderApp(<Landing />, { auth });

    await loginForm("ana@example.com", "clave-larga-123");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));
    await userEvent.click(await screen.findByRole("button", { name: "Reenviar el correo de verificación" }));

    await waitFor(() => expect(auth.spies.resendVerification).toHaveBeenCalledWith("ana@example.com"));
    expect(await screen.findByText(/te enviamos el correo de nuevo/)).toBeInTheDocument();
  });

  it("registers with alias, email and password and asks to confirm the email, without saying whether it already existed", async () => {
    const auth = fakeAuth(null);
    renderApp(<Landing />, { auth });

    await userEvent.click(screen.getByRole("tab", { name: "Crear cuenta" }));
    await userEvent.type(screen.getByLabelText("Alias"), "ana_juega");
    await userEvent.type(screen.getByLabelText("Email"), "ana@example.com");
    await userEvent.type(screen.getByLabelText("Contraseña"), "clave-larga-123");
    await userEvent.click(screen.getByRole("button", { name: "Crear cuenta" }));

    await waitFor(() => expect(auth.spies.register).toHaveBeenCalledWith("ana_juega", "ana@example.com", "clave-larga-123"));
    expect(await screen.findByText(/Te enviamos un correo para confirmar tu email/)).toBeInTheDocument();
    expect(screen.getByLabelText("Alias o email")).toHaveValue("ana_juega"); // vuelve a Entrar con el alias ya escrito
  });

  it("tells the player when the alias is taken", async () => {
    const auth = fakeAuth(null);
    auth.spies.register.mockRejectedValueOnce(new AuthError("alias_taken"));
    renderApp(<Landing />, { auth });

    await userEvent.click(screen.getByRole("tab", { name: "Crear cuenta" }));
    await userEvent.type(screen.getByLabelText("Alias"), "ana_juega");
    await userEvent.type(screen.getByLabelText("Email"), "ana@example.com");
    await userEvent.type(screen.getByLabelText("Contraseña"), "clave-larga-123");
    await userEvent.click(screen.getByRole("button", { name: "Crear cuenta" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Ese alias ya está en uso");
  });

  it("shows the Google button only when the identity service has Google configured", async () => {
    renderApp(<Landing />, { auth: fakeAuth(null, { google: false }) });
    await screen.findByRole("button", { name: "Entrar" });
    expect(screen.queryByRole("button", { name: "Entrar con Google" })).not.toBeInTheDocument();
  });

  it("starts the Google sign-in remembering where to come back", async () => {
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign, origin: "http://localhost:5173", pathname: "/historial", search: "" });
    try {
      renderApp(<Landing />, { auth: fakeAuth(null, { google: true }) });

      await userEvent.click(await screen.findByRole("button", { name: "Entrar con Google" }));

      expect(assign).toHaveBeenCalledWith(expect.stringContaining("returnUrl=http%3A%2F%2Flocalhost%3A5173%2Fauth%2Fcallback"));
      expect(sessionStorage.getItem("casino.returnTo")).toBe("/historial");
    } finally {
      vi.unstubAllGlobals();
      sessionStorage.clear();
    }
  });

  it("links to the password recovery", async () => {
    renderApp(<Landing />, { auth: fakeAuth(null) });

    expect(await screen.findByRole("link", { name: "¿Olvidaste tu contraseña?" })).toHaveAttribute("href", "/recuperar");
  });
});
