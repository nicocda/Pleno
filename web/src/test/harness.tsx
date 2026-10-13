import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { MemoryRouter } from "react-router-dom";
import { ApiProvider } from "../api/ApiProvider";
import { ApiError, type ApiClient } from "../api/client";
import { AuthProvider } from "../auth/AuthContext";
import type { AuthClient, AuthUser } from "../auth/identity";
import { ToastProvider } from "../components/Toasts";
import { RealtimeProvider, type ConnectionFactory, type HubConnectionLike } from "../realtime/RealtimeProvider";
import { PerfProvider } from "../perf/PerfProvider";
import { ThemeProvider } from "../theme/ThemeProvider";

/** Un jugador ya autenticado, con token vigente. */
export function fakeUser(overrides: Partial<{ expired: boolean; name: string; roles: string[] }> = {}): AuthUser {
  return {
    access_token: "token-de-prueba",
    expired: overrides.expired ?? false,
    profile: { preferred_username: overrides.name ?? "jugador1", sub: "0a1b2c3d-0001-4000-8000-000000000001", roles: overrides.roles ?? ["player"] },
  };
}

export interface FakeAuth extends AuthClient {
  spies: {
    signIn: ReturnType<typeof vi.fn>;
    signInWithCode: ReturnType<typeof vi.fn>;
    signOut: ReturnType<typeof vi.fn>;
    register: ReturnType<typeof vi.fn>;
    verifyEmail: ReturnType<typeof vi.fn>;
    resendVerification: ReturnType<typeof vi.fn>;
    forgotPassword: ReturnType<typeof vi.fn>;
    resetPassword: ReturnType<typeof vi.fn>;
  };
  emitUserLoaded(user: AuthUser): void;
  emitUserUnloaded(): void;
}

/** Cliente de identidad simulado: iniciar sesion carga al usuario y cerrarla lo descarga, como el real. */
export function fakeAuth(user: AuthUser | null = fakeUser(), options: { google?: boolean } = {}): FakeAuth {
  const loaded = new Set<(user: AuthUser) => void>();
  const unloaded = new Set<() => void>();
  const emitUserLoaded = (next: AuthUser) => loaded.forEach((cb) => cb(next));
  const emitUserUnloaded = () => unloaded.forEach((cb) => cb());
  const signIn = vi.fn(async (_login: string, _password: string) => {
    const next = fakeUser();
    emitUserLoaded(next);
    return next;
  });
  const signInWithCode = vi.fn(async (_code: string) => {
    const next = fakeUser();
    emitUserLoaded(next);
    return next;
  });
  const signOut = vi.fn(async () => emitUserUnloaded());
  const register = vi.fn(async (_alias: string, _email: string, _password: string) => undefined);
  const verifyEmail = vi.fn(async (_token: string) => undefined);
  const resendVerification = vi.fn(async (_email: string) => undefined);
  const forgotPassword = vi.fn(async (_email: string) => undefined);
  const resetPassword = vi.fn(async (_token: string, _newPassword: string) => undefined);
  return {
    spies: { signIn, signInWithCode, signOut, register, verifyEmail, resendVerification, forgotPassword, resetPassword },
    getUser: vi.fn(async () => user),
    signIn,
    signInWithCode,
    signOut,
    getAccessToken: vi.fn(async () => (user && !user.expired ? user.access_token : null)),
    register,
    verifyEmail,
    resendVerification,
    forgotPassword,
    resetPassword,
    providers: vi.fn(async () => ({ google: options.google ?? false })),
    googleStartUrl: (returnUrl: string) => `http://identidad.test/identity/google/start?returnUrl=${encodeURIComponent(returnUrl)}`,
    events: {
      addUserLoaded: (cb) => void loaded.add(cb),
      removeUserLoaded: (cb) => void loaded.delete(cb),
      addUserUnloaded: (cb) => void unloaded.add(cb),
      removeUserUnloaded: (cb) => void unloaded.delete(cb),
    },
    emitUserLoaded,
    emitUserUnloaded,
  };
}

type Handler = (body?: unknown, headers?: Record<string, string>) => unknown;

export interface FakeApi extends ApiClient {
  calls: { method: "GET" | "POST" | "PUT" | "DELETE"; path: string; body?: unknown; headers?: Record<string, string> }[];
}

/** API simulada: cada ruta es una funcion que devuelve el cuerpo o lanza un ApiError. Lo no declarado responde 404. */
export function fakeApi(routes: Record<string, Handler>): FakeApi {
  const calls: FakeApi["calls"] = [];
  const respond = async <T,>(method: "GET" | "POST" | "PUT" | "DELETE", path: string, body?: unknown, headers?: Record<string, string>): Promise<T> => {
    calls.push({ method, path, body, headers });
    const handler = routes[`${method} ${path.split("?")[0]}`];
    if (!handler) throw new ApiError(404, "NotFound");
    return (await handler(body, headers)) as T;
  };
  return {
    calls,
    get: <T,>(path: string) => respond<T>("GET", path),
    post: <T,>(path: string, body?: unknown, headers?: Record<string, string>) => respond<T>("POST", path, body, headers),
    put: <T,>(path: string, body?: unknown, headers?: Record<string, string>) => respond<T>("PUT", path, body, headers),
    delete: <T,>(path: string) => respond<T>("DELETE", path),
  };
}

export class FakeHub implements HubConnectionLike {
  readonly handlers = new Map<string, (payload: never) => void>();
  private reconnecting: (() => void) | null = null;
  private reconnected: (() => void) | null = null;
  private closed: (() => void) | null = null;
  started = false;
  stopped = false;
  startError: Error | null = null;

  on(method: string, handler: (payload: never) => void) {
    this.handlers.set(method, handler);
  }
  onreconnecting(handler: () => void) {
    this.reconnecting = handler;
  }
  onreconnected(handler: () => void) {
    this.reconnected = handler;
  }
  onclose(handler: () => void) {
    this.closed = handler;
  }
  async start() {
    if (this.startError) throw this.startError;
    this.started = true;
  }
  async stop() {
    this.stopped = true;
  }

  emit(method: string, payload: unknown) {
    this.handlers.get(method)?.(payload as never);
  }
  dropConnection() {
    this.reconnecting?.();
  }
  recoverConnection() {
    this.reconnected?.();
  }
  closeConnection() {
    this.closed?.();
  }
}

export interface AppOptions {
  auth?: AuthClient;
  api?: ApiClient;
  identityApi?: ApiClient;
  hub?: FakeHub;
  route?: string;
}

/** Monta una pagina con todos los providers reales y las piezas externas simuladas. */
export function renderApp(ui: ReactElement, options: AppOptions = {}) {
  const hub = options.hub ?? new FakeHub();
  const connectionFactory: ConnectionFactory = () => hub;
  const auth = options.auth ?? fakeAuth();
  const api = options.api ?? fakeApi({});

  const view = render(
    <PerfProvider>
    <ThemeProvider>
      <ToastProvider>
        <AuthProvider client={auth}>
          <MemoryRouter initialEntries={[options.route ?? "/"]}>
            <ApiProvider client={api} identityClient={options.identityApi ?? fakeApi({})}>
              <RealtimeProvider connectionFactory={connectionFactory}>{ui}</RealtimeProvider>
            </ApiProvider>
          </MemoryRouter>
        </AuthProvider>
      </ToastProvider>
    </ThemeProvider>
    </PerfProvider>,
  );
  return { ...view, hub, api, auth };
}
