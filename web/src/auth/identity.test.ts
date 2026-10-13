import { AuthError, createAuthClient } from "./identity";

const b64 = (value: unknown) => btoa(JSON.stringify(value)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const jwt = (claims: Record<string, unknown>) => `${b64({ alg: "RS256" })}.${b64(claims)}.firma`;
const TOKEN = jwt({ sub: "0a1b2c3d-0001-4000-8000-000000000001", preferred_username: "jugador1", role: ["player", "cashier"] });
const SINGLE_ROLE = jwt({ sub: "0a1b2c3d-0002-4000-8000-000000000002", preferred_username: "jugador2", role: "player" });

class MemoryStorage {
  private data = new Map<string, string>();
  getItem = (key: string) => this.data.get(key) ?? null;
  setItem = (key: string, value: string) => void this.data.set(key, value);
  removeItem = (key: string) => void this.data.delete(key);
}

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function setup(handler: (url: string, init: RequestInit) => Response | Promise<Response>, clock = { now: 1_000 }) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init: init ?? {} });
    return handler(String(input), init ?? {});
  }) as unknown as typeof fetch;
  const storage = new MemoryStorage();
  const client = createAuthClient({ baseUrl: "http://identidad.test", fetchImpl, storage, now: () => clock.now });
  return { client, calls, storage, clock };
}

const form = (init: RequestInit) => Object.fromEntries(new URLSearchParams(String(init.body)));

describe("createAuthClient", () => {
  it("signs in with the password grant and reads the claims from the token, including the roles", async () => {
    const { client, calls } = setup(() => json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }));

    const user = await client.signIn("jugador1", "clave-larga-123");

    expect(user.profile).toEqual({ sub: "0a1b2c3d-0001-4000-8000-000000000001", preferred_username: "jugador1", roles: ["player", "cashier"] });
    expect(user.expired).toBe(false);
    expect(calls[0]!.url).toBe("http://identidad.test/connect/token");
    expect(form(calls[0]!.init)).toMatchObject({ grant_type: "password", client_id: "casino-web", username: "jugador1", password: "clave-larga-123" });
  });

  it("accepts a single role given as a string", async () => {
    const { client } = setup(() => json(200, { access_token: SINGLE_ROLE, refresh_token: "r1", expires_in: 600 }));

    expect((await client.signIn("jugador2", "x")).profile.roles).toEqual(["player"]);
  });

  it("keeps the session across reloads and never stores the password", async () => {
    const { client, storage } = setup(() => json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }));
    await client.signIn("jugador1", "clave-larga-123");

    expect(storage.getItem("casino.session")).not.toContain("clave-larga-123");
    expect((await client.getUser())?.profile.preferred_username).toBe("jugador1");
  });

  it.each([
    [400, { error: "invalid_grant", error_description: "Usuario o clave incorrectos." }, "invalid_credentials"],
    [400, { error: "invalid_grant", error_description: "email_not_verified" }, "email_not_verified"],
    [429, {}, "rate_limited"],
    [503, {}, "unreachable"],
  ])("maps a %i answer to %s", async (status, body, code) => {
    const { client } = setup(() => json(status, body));

    await expect(client.signIn("a", "b")).rejects.toMatchObject({ code: expect.stringMatching(code) });
  });

  it("reports an unreachable server when the network fails", async () => {
    const { client } = setup(() => {
      throw new TypeError("Failed to fetch");
    });

    await expect(client.signIn("a", "b")).rejects.toEqual(new AuthError("unreachable"));
  });

  it("signs in with the one-time code of the Google flow", async () => {
    const { client, calls } = setup(() => json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }));

    await client.signInWithCode("codigo-1");

    expect(form(calls[0]!.init)).toMatchObject({ grant_type: "urn:casino:grant_type:external_code", code: "codigo-1" });
  });

  it("renews the token with the refresh token before it expires, and tells listeners", async () => {
    const clock = { now: 1_000 };
    let n = 0;
    const { client, calls } = setup(() => json(200, { access_token: n++ === 0 ? TOKEN : SINGLE_ROLE, refresh_token: `r${n}`, expires_in: 600 }), clock);
    const onLoaded = vi.fn();
    client.events.addUserLoaded(onLoaded);
    await client.signIn("jugador1", "x");

    clock.now += 560; // faltan menos de 60 s
    const token = await client.getAccessToken();

    expect(token).toBe(SINGLE_ROLE);
    expect(form(calls[1]!.init)).toMatchObject({ grant_type: "refresh_token", refresh_token: "r1" });
    expect(onLoaded).toHaveBeenCalledTimes(2);
  });

  it("shares one refresh between simultaneous callers", async () => {
    const clock = { now: 1_000 };
    const { client, calls } = setup(() => json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }), clock);
    await client.signIn("jugador1", "x");

    clock.now += 590;
    await Promise.all([client.getAccessToken(), client.getAccessToken(), client.getAccessToken()]);

    expect(calls.filter((c) => form(c.init).grant_type === "refresh_token")).toHaveLength(1);
  });

  it("closes the session when the server rejects the refresh token", async () => {
    const clock = { now: 1_000 };
    let n = 0;
    const { client, storage } = setup(() => (n++ === 0 ? json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }) : json(400, { error: "invalid_grant" })), clock);
    const onUnloaded = vi.fn();
    client.events.addUserUnloaded(onUnloaded);
    await client.signIn("jugador1", "x");

    clock.now += 600;

    expect(await client.getAccessToken()).toBeNull();
    expect(storage.getItem("casino.session")).toBeNull();
    expect(onUnloaded).toHaveBeenCalled();
  });

  it("keeps the session when the refresh only failed because of the network", async () => {
    const clock = { now: 1_000 };
    let n = 0;
    const { client } = setup(() => {
      if (n++ === 0) return json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 });
      throw new TypeError("Failed to fetch");
    }, clock);
    await client.signIn("jugador1", "x");

    clock.now += 570; // dentro del margen pero todavia no vencio

    expect(await client.getAccessToken()).toBe(TOKEN);
  });

  it("signs out: forgets the session and tells listeners", async () => {
    const { client, storage } = setup(() => json(200, { access_token: TOKEN, refresh_token: "r1", expires_in: 600 }));
    const onUnloaded = vi.fn();
    client.events.addUserUnloaded(onUnloaded);
    await client.signIn("jugador1", "x");

    await client.signOut();

    expect(storage.getItem("casino.session")).toBeNull();
    expect(await client.getUser()).toBeNull();
    expect(onUnloaded).toHaveBeenCalledOnce();
  });

  it.each([
    ["alias_taken", 409],
    ["invalid_alias", 400],
    ["invalid_email", 400],
    ["weak_password", 400],
  ])("maps the registration error %s", async (error, status) => {
    const { client } = setup(() => json(status, { error }));

    await expect(client.register("a", "b@c.d", "x")).rejects.toMatchObject({ code: error });
  });

  it("calls the account endpoints with JSON bodies", async () => {
    const { client, calls } = setup(() => new Response(null, { status: 204 }));

    await client.register("ana", "ana@example.com", "clave-larga-123");
    await client.verifyEmail("t1");
    await client.resendVerification("ana@example.com");
    await client.forgotPassword("ana@example.com");
    await client.resetPassword("t2", "otra-clave-larga");

    expect(calls.map((c) => c.url)).toEqual([
      "http://identidad.test/identity/register",
      "http://identidad.test/identity/verify-email",
      "http://identidad.test/identity/resend-verification",
      "http://identidad.test/identity/forgot-password",
      "http://identidad.test/identity/reset-password",
    ]);
    expect(JSON.parse(String(calls[4]!.init.body))).toEqual({ token: "t2", newPassword: "otra-clave-larga" });
  });

  it("asks which providers exist, and assumes none if the service does not answer", async () => {
    expect(await setup(() => json(200, { google: true })).client.providers()).toEqual({ google: true });
    expect(
      await setup(() => {
        throw new TypeError("Failed to fetch");
      }).client.providers(),
    ).toEqual({ google: false });
  });

  it("builds the Google start URL with the return address encoded", () => {
    const { client } = setup(() => json(200, {}));

    expect(client.googleStartUrl("http://localhost:5173/auth/callback")).toBe(
      "http://identidad.test/identity/google/start?returnUrl=http%3A%2F%2Flocalhost%3A5173%2Fauth%2Fcallback",
    );
  });
});
