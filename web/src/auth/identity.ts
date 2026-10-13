import { config } from "../config";

/** La sesion del jugador: lo que el front necesita saber del token. El token es un JWT del servicio de identidad. */
export interface AuthUser {
  access_token: string;
  expired: boolean;
  profile: { sub: string; preferred_username?: string; roles: string[] };
}

export type AuthErrorCode =
  | "invalid_credentials"
  | "email_not_verified"
  | "unreachable"
  | "rate_limited"
  | "invalid_code"
  | "alias_taken"
  | "invalid_alias"
  | "invalid_email"
  | "weak_password"
  | "invalid_token"
  | "unknown";

export class AuthError extends Error {
  readonly code: AuthErrorCode;

  constructor(code: AuthErrorCode) {
    super(code);
    this.name = "AuthError";
    this.code = code;
  }
}

/** Lo que la app necesita del servicio de identidad. Permite sustituirlo en las pruebas sin levantar nada. */
export interface AuthClient {
  getUser(): Promise<AuthUser | null>;
  /** Entra con alias o email y contraseña. Lanza `AuthError`. */
  signIn(login: string, password: string): Promise<AuthUser>;
  /** Cambia el codigo de un solo uso que dejo el login con Google por una sesion. */
  signInWithCode(code: string): Promise<AuthUser>;
  signOut(): Promise<void>;
  /** Token vigente (lo renueva si esta por vencer) o null si no hay sesion. */
  getAccessToken(): Promise<string | null>;
  register(alias: string, email: string, password: string): Promise<void>;
  verifyEmail(token: string): Promise<void>;
  resendVerification(email: string): Promise<void>;
  forgotPassword(email: string): Promise<void>;
  resetPassword(token: string, newPassword: string): Promise<void>;
  providers(): Promise<{ google: boolean }>;
  googleStartUrl(returnUrl: string): string;
  events: {
    addUserLoaded(cb: (user: AuthUser) => void): void;
    removeUserLoaded(cb: (user: AuthUser) => void): void;
    addUserUnloaded(cb: () => void): void;
    removeUserUnloaded(cb: () => void): void;
  };
}

interface StoredSession {
  access_token: string;
  refresh_token?: string;
  /** Segundos desde epoch en que vence el access token. */
  expires_at: number;
}

export interface AuthClientOptions {
  baseUrl?: string;
  clientId?: string;
  fetchImpl?: typeof fetch;
  storage?: Pick<Storage, "getItem" | "setItem" | "removeItem">;
  now?: () => number;
}

const STORAGE_KEY = "casino.session";
/** Se renueva un minuto antes de que venza. */
const REFRESH_MARGIN_S = 60;
const RETRY_AFTER_ERROR_MS = 30_000;

function decodeProfile(token: string): AuthUser["profile"] {
  const part = token.split(".")[1] ?? "";
  const json = atob(part.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(part.length / 4) * 4, "="));
  const claims = JSON.parse(json) as { sub: string; preferred_username?: string; role?: string | string[] };
  const roles = claims.role === undefined ? [] : Array.isArray(claims.role) ? claims.role : [claims.role];
  return { sub: claims.sub, preferred_username: claims.preferred_username, roles };
}

/**
 * Cliente del servicio de identidad: entra con alias/email y contraseña (o con el codigo de Google) y renueva el token solo.
 * La sesion vive en sessionStorage: se pierde al cerrar la pestaña. Nunca se guarda la contraseña ni el email.
 */
export function createAuthClient(options: AuthClientOptions = {}): AuthClient {
  const baseUrl = (options.baseUrl ?? config.identityUrl).replace(/\/$/, "");
  const clientId = options.clientId ?? config.oidcClientId;
  const doFetch: typeof fetch = (input, init) => (options.fetchImpl ?? fetch)(input, init);
  const storage = options.storage ?? window.sessionStorage;
  const now = options.now ?? (() => Date.now() / 1000);

  const loaded = new Set<(user: AuthUser) => void>();
  const unloaded = new Set<() => void>();
  let refreshing: Promise<AuthUser | null> | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const read = (): StoredSession | null => {
    try {
      const raw = storage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as StoredSession) : null;
    } catch {
      return null;
    }
  };
  const write = (session: StoredSession | null) => {
    try {
      if (session) storage.setItem(STORAGE_KEY, JSON.stringify(session));
      else storage.removeItem(STORAGE_KEY);
    } catch {
      // Sin almacenamiento disponible la sesion dura lo que dure la pagina.
    }
  };

  const toUser = (session: StoredSession): AuthUser => ({
    access_token: session.access_token,
    expired: now() >= session.expires_at,
    profile: decodeProfile(session.access_token),
  });

  const schedule = (session: StoredSession) => {
    clearTimeout(timer);
    if (!session.refresh_token) return;
    const wait = Math.max(0, (session.expires_at - REFRESH_MARGIN_S - now()) * 1000);
    timer = setTimeout(() => void refresh(), wait);
  };

  const clear = () => {
    clearTimeout(timer);
    write(null);
    unloaded.forEach((cb) => cb());
  };

  const accept = (body: { access_token: string; refresh_token?: string; expires_in: number }): AuthUser => {
    const session: StoredSession = { access_token: body.access_token, refresh_token: body.refresh_token, expires_at: now() + body.expires_in };
    write(session);
    schedule(session);
    const user = toUser(session);
    loaded.forEach((cb) => cb(user));
    return user;
  };

  const postToken = async (form: Record<string, string>) => {
    let response: Response;
    try {
      response = await doFetch(`${baseUrl}/connect/token`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
        body: new URLSearchParams({ client_id: clientId, ...form }),
      });
    } catch {
      throw new AuthError("unreachable");
    }
    if (response.status === 429) throw new AuthError("rate_limited");
    const body = (await response.json().catch(() => ({}))) as { error?: string; error_description?: string };
    return { response, body };
  };

  async function refresh(): Promise<AuthUser | null> {
    refreshing ??= (async () => {
      try {
        const session = read();
        if (!session?.refresh_token) return null;
        const { response, body } = await postToken({ grant_type: "refresh_token", refresh_token: session.refresh_token });
        if (response.ok) return accept(body as never);
        clear(); // el servidor lo rechazo: la sesion ya no vale
        return null;
      } catch {
        // Sin red no se pierde la sesion: se reintenta mas tarde.
        timer = setTimeout(() => void refresh(), RETRY_AFTER_ERROR_MS);
        return null;
      } finally {
        // Siempre se libera, tambien cuando no habia sesion que renovar: si no, una renovacion posterior devolveria este resultado viejo.
        refreshing = null;
      }
    })();
    return refreshing;
  }

  const postJson = async (path: string, body: unknown) => {
    let response: Response;
    try {
      response = await doFetch(`${baseUrl}${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify(body),
      });
    } catch {
      throw new AuthError("unreachable");
    }
    if (response.ok) return;
    if (response.status === 429) throw new AuthError("rate_limited");
    const problem = (await response.json().catch(() => ({}))) as { error?: string };
    const known: AuthErrorCode[] = ["alias_taken", "invalid_alias", "invalid_email", "weak_password", "invalid_token"];
    throw new AuthError(known.find((code) => code === problem.error) ?? "unknown");
  };

  const signInWith = async (form: Record<string, string>, failure: AuthErrorCode): Promise<AuthUser> => {
    const { response, body } = await postToken(form);
    if (response.ok) return accept(body as never);
    if (body.error_description === "email_not_verified") throw new AuthError("email_not_verified");
    throw new AuthError(response.status >= 500 ? "unreachable" : failure);
  };

  return {
    async getUser() {
      const session = read();
      if (!session) return null;
      if (now() >= session.expires_at - REFRESH_MARGIN_S) {
        const renewed = await refresh();
        if (renewed) return renewed;
        const current = read();
        return current ? toUser(current) : null;
      }
      schedule(session);
      return toUser(session);
    },
    signIn: (login, password) => signInWith({ grant_type: "password", username: login, password, scope: "offline_access" }, "invalid_credentials"),
    signInWithCode: (code) => signInWith({ grant_type: "urn:casino:grant_type:external_code", code, scope: "offline_access" }, "invalid_code"),
    async signOut() {
      clear();
    },
    async getAccessToken() {
      const session = read();
      if (!session) return null;
      if (now() < session.expires_at - REFRESH_MARGIN_S) return session.access_token;
      const renewed = await refresh();
      if (renewed) return renewed.access_token;
      const current = read();
      return current && now() < current.expires_at ? current.access_token : null;
    },
    register: (alias, email, password) => postJson("/identity/register", { alias, email, password }),
    verifyEmail: (token) => postJson("/identity/verify-email", { token }),
    resendVerification: (email) => postJson("/identity/resend-verification", { email }),
    forgotPassword: (email) => postJson("/identity/forgot-password", { email }),
    resetPassword: (token, newPassword) => postJson("/identity/reset-password", { token, newPassword }),
    async providers() {
      try {
        const response = await doFetch(`${baseUrl}/identity/providers`, { headers: { Accept: "application/json" } });
        return response.ok ? ((await response.json()) as { google: boolean }) : { google: false };
      } catch {
        return { google: false };
      }
    },
    googleStartUrl: (returnUrl) => `${baseUrl}/identity/google/start?returnUrl=${encodeURIComponent(returnUrl)}`,
    events: {
      addUserLoaded: (cb) => void loaded.add(cb),
      removeUserLoaded: (cb) => void loaded.delete(cb),
      addUserUnloaded: (cb) => void unloaded.add(cb),
      removeUserUnloaded: (cb) => void unloaded.delete(cb),
    },
  };
}
