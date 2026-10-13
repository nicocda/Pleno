import { act, render, screen, waitFor } from "@testing-library/react";
import { AuthProvider, useAuth } from "./AuthContext";
import { fakeAuth, fakeUser } from "../test/harness";

function Probe() {
  const auth = useAuth();
  return (
    <div>
      <span data-testid="status">{auth.status}</span>
      <span data-testid="name">{auth.displayName ?? "-"}</span>
      <button onClick={() => void auth.signIn("jugador1", "clave-larga-123")}>signin</button>
      <button onClick={() => void auth.signInWithCode("codigo")}>code</button>
      <button onClick={() => void auth.logout()}>logout</button>
      <button onClick={() => auth.expire()}>expire</button>
      <button onClick={async () => (document.title = String(await auth.getAccessToken()))}>token</button>
    </div>
  );
}

const mount = (client: ReturnType<typeof fakeAuth>) =>
  render(
    <AuthProvider client={client}>
      <Probe />
    </AuthProvider>,
  );

describe("AuthProvider", () => {
  it("starts loading and becomes authenticated when there is a valid stored session", async () => {
    mount(fakeAuth(fakeUser({ name: "alicia" })));

    expect(screen.getByTestId("status")).toHaveTextContent("loading");
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("authenticated"));
    expect(screen.getByTestId("name")).toHaveTextContent("alicia");
  });

  it("is anonymous when there is no session", async () => {
    mount(fakeAuth(null));

    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));
  });

  it("treats an expired stored session as anonymous", async () => {
    mount(fakeAuth(fakeUser({ expired: true })));

    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));
  });

  it("reacts when the session is renewed or removed", async () => {
    const client = fakeAuth(null);
    mount(client);
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));

    act(() => client.emitUserLoaded(fakeUser()));
    expect(screen.getByTestId("status")).toHaveTextContent("authenticated");

    act(() => client.emitUserUnloaded());
    expect(screen.getByTestId("status")).toHaveTextContent("anonymous");
  });

  it("signs in with the alias or email and the password, and becomes authenticated", async () => {
    const client = fakeAuth(null);
    mount(client);
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));

    screen.getByText("signin").click();

    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("authenticated"));
    expect(client.spies.signIn).toHaveBeenCalledWith("jugador1", "clave-larga-123");
  });

  it("signs in with the one-time code that Google leaves", async () => {
    const client = fakeAuth(null);
    mount(client);
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));

    screen.getByText("code").click();

    await waitFor(() => expect(client.spies.signInWithCode).toHaveBeenCalledWith("codigo"));
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("authenticated"));
  });

  it("signs out and goes back to anonymous", async () => {
    const client = fakeAuth();
    mount(client);
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("authenticated"));

    screen.getByText("logout").click();

    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));
    expect(client.spies.signOut).toHaveBeenCalledOnce();
  });

  it("closes the session when the server says it no longer holds (401)", async () => {
    const client = fakeAuth();
    mount(client);
    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("authenticated"));

    screen.getByText("expire").click();

    await waitFor(() => expect(screen.getByTestId("status")).toHaveTextContent("anonymous"));
  });

  it("gives the current token, and null when it has expired", async () => {
    const fresh = fakeAuth(fakeUser());
    const { unmount } = mount(fresh);
    screen.getByText("token").click();
    await waitFor(() => expect(document.title).toBe("token-de-prueba"));
    unmount();

    const expired = fakeAuth(fakeUser({ expired: true }));
    mount(expired);
    screen.getByText("token").click();
    await waitFor(() => expect(document.title).toBe("null"));
  });
});
