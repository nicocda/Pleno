import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router-dom";
import { AuthError } from "../auth/identity";
import { fakeAuth, renderApp } from "../test/harness";
import { AuthCallback } from "./AuthCallback";
import { RecoverPassword } from "./RecoverPassword";
import { VerifyEmail } from "./VerifyEmail";

describe("VerifyEmail", () => {
  it("confirms the email with the token of the link", async () => {
    const auth = fakeAuth(null);
    renderApp(<VerifyEmail />, { auth, route: "/verificar?token=abc" });

    expect(await screen.findByText("¡Listo! Tu email quedó confirmado.")).toBeInTheDocument();
    expect(auth.spies.verifyEmail).toHaveBeenCalledWith("abc");
    expect(screen.getByRole("link", { name: "Entrar" })).toHaveAttribute("href", "/");
  });

  it("explains an invalid or expired link", async () => {
    const auth = fakeAuth(null);
    auth.spies.verifyEmail.mockRejectedValueOnce(new AuthError("invalid_token"));
    renderApp(<VerifyEmail />, { auth, route: "/verificar?token=viejo" });

    expect(await screen.findByRole("alert")).toHaveTextContent("El enlace no es válido o ya venció");
  });

  it("does not call the server when the link has no token", async () => {
    const auth = fakeAuth(null);
    renderApp(<VerifyEmail />, { auth, route: "/verificar" });

    expect(await screen.findByRole("alert")).toHaveTextContent("El enlace no es válido");
    expect(auth.spies.verifyEmail).not.toHaveBeenCalled();
  });
});

describe("RecoverPassword", () => {
  it("asks for the email and always answers the same", async () => {
    const auth = fakeAuth(null);
    renderApp(<RecoverPassword />, { auth, route: "/recuperar" });

    await userEvent.type(screen.getByLabelText("Email"), "ana@example.com");
    await userEvent.click(screen.getByRole("button", { name: "Enviar enlace" }));

    await waitFor(() => expect(auth.spies.forgotPassword).toHaveBeenCalledWith("ana@example.com"));
    expect(await screen.findByText(/Si hay una cuenta con ese email/)).toBeInTheDocument();
  });

  it("chooses the new password with the token of the link", async () => {
    const auth = fakeAuth(null);
    renderApp(<RecoverPassword />, { auth, route: "/recuperar?token=t1" });

    await userEvent.type(screen.getByLabelText("Contraseña nueva"), "otra-clave-larga-1");
    await userEvent.click(screen.getByRole("button", { name: "Cambiar contraseña" }));

    await waitFor(() => expect(auth.spies.resetPassword).toHaveBeenCalledWith("t1", "otra-clave-larga-1"));
    expect(await screen.findByText(/tu contraseña cambió/)).toBeInTheDocument();
  });

  it("shows the reason when the link is no longer valid and lets the player try again", async () => {
    const auth = fakeAuth(null);
    auth.spies.resetPassword.mockRejectedValueOnce(new AuthError("invalid_token"));
    renderApp(<RecoverPassword />, { auth, route: "/recuperar?token=viejo" });

    await userEvent.type(screen.getByLabelText("Contraseña nueva"), "otra-clave-larga-1");
    await userEvent.click(screen.getByRole("button", { name: "Cambiar contraseña" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("El enlace no es válido o ya venció");
    expect(screen.getByRole("button", { name: "Cambiar contraseña" })).toBeEnabled();
  });
});

describe("AuthCallback", () => {
  const routes = (
    <Routes>
      <Route path="/auth/callback" element={<AuthCallback />} />
      <Route path="*" element={<p>destino</p>} />
    </Routes>
  );

  it("exchanges the Google code once and goes back to where the player was", async () => {
    sessionStorage.setItem("casino.returnTo", "/historial");
    const auth = fakeAuth(null);
    renderApp(routes, { auth, route: "/auth/callback?code=c1" });

    expect(await screen.findByText("destino")).toBeInTheDocument();
    expect(auth.spies.signInWithCode).toHaveBeenCalledOnce();
    expect(auth.spies.signInWithCode).toHaveBeenCalledWith("c1");
    expect(sessionStorage.getItem("casino.returnTo")).toBeNull();
  });

  it("never goes back to another site, only to own routes", async () => {
    sessionStorage.setItem("casino.returnTo", "//otro-sitio.test/robo");
    renderApp(routes, { auth: fakeAuth(null), route: "/auth/callback?code=c1" });

    expect(await screen.findByText("destino")).toBeInTheDocument();
  });

  it("shows an error when the code is rejected", async () => {
    const auth = fakeAuth(null);
    auth.spies.signInWithCode.mockRejectedValueOnce(new AuthError("invalid_code"));
    renderApp(routes, { auth, route: "/auth/callback?code=viejo" });

    expect(await screen.findByRole("alert")).toHaveTextContent("No pudimos iniciar tu sesión");
    expect(screen.getByRole("link", { name: "Volver a intentar" })).toHaveAttribute("href", "/");
  });

  it("shows an error when Google comes back without a code", async () => {
    const auth = fakeAuth(null);
    renderApp(routes, { auth, route: "/auth/callback?error=google_failed" });

    expect(await screen.findByRole("alert")).toHaveTextContent("No pudimos iniciar tu sesión");
    expect(auth.spies.signInWithCode).not.toHaveBeenCalled();
  });
});
