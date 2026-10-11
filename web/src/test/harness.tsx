import type { User } from "oidc-client-ts";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { MemoryRouter } from "react-router-dom";
import { ApiProvider } from "../api/ApiProvider";
import { ApiError, type ApiClient } from "../api/client";
import { AuthProvider } from "../auth/AuthContext";
import type { OidcManager } from "../auth/oidc";
import { ToastProvider } from "../components/Toasts";
import { RealtimeProvider, type ConnectionFactory, type HubConnectionLike } from "../realtime/RealtimeProvider";
import { PerfProvider } from "../perf/PerfProvider";
import { ThemeProvider } from "../theme/ThemeProvider";

/** Un jugador ya autenticado, con token vigente. */
export function fakeUser(overrides: Partial<{ expired: boolean; name: string }> = {}): User {
  return {
    access_token: "token-de-prueba",
    expired: overrides.expired ?? false,
    profile: { preferred_username: overrides.name ?? "jugador1", sub: "0a1b2c3d-0001-4000-8000-000000000001" },
    state: undefined,
  } as unknown as User;
}

export interface FakeOidc extends OidcManager {
  spies: { signinRedirect: ReturnType<typeof vi.fn>; signoutRedirect: ReturnType<typeof vi.fn>; signinRedirectCallback: ReturnType<typeof vi.fn> };
  emitUserLoaded(user: User): void;
  emitUserUnloaded(): void;
}

export function fakeOidc(user: User | null = fakeUser()): FakeOidc {
  const loaded = new Set<(user: User) => void>();
  const unloaded = new Set<() => void>();
  const signinRedirect = vi.fn(async (_args?: { state?: unknown }) => undefined);
  const signoutRedirect = vi.fn(async () => undefined);
  const signinRedirectCallback = vi.fn(async () => ({ ...fakeUser(), state: { returnTo: "/ruleta" } }) as unknown as User);
  return {
    spies: { signinRedirect, signoutRedirect, signinRedirectCallback },
    getUser: vi.fn(async () => user),
    signinRedirect,
    signoutRedirect,
    signinRedirectCallback,
    signinSilent: vi.fn(async () => user),
    events: {
      addUserLoaded: (cb) => void loaded.add(cb),
      removeUserLoaded: (cb) => void loaded.delete(cb),
      addUserUnloaded: (cb) => void unloaded.add(cb),
      removeUserUnloaded: (cb) => void unloaded.delete(cb),
      addSilentRenewError: () => undefined,
      removeSilentRenewError: () => undefined,
    },
    emitUserLoaded: (next) => loaded.forEach((cb) => cb(next)),
    emitUserUnloaded: () => unloaded.forEach((cb) => cb()),
  } as FakeOidc;
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
  oidc?: OidcManager;
  api?: ApiClient;
  hub?: FakeHub;
  route?: string;
}

/** Monta una pagina con todos los providers reales y las piezas externas simuladas. */
export function renderApp(ui: ReactElement, options: AppOptions = {}) {
  const hub = options.hub ?? new FakeHub();
  const connectionFactory: ConnectionFactory = () => hub;
  const oidc = options.oidc ?? fakeOidc();
  const api = options.api ?? fakeApi({});

  const view = render(
    <PerfProvider>
    <ThemeProvider>
      <ToastProvider>
        <AuthProvider manager={oidc}>
          <MemoryRouter initialEntries={[options.route ?? "/"]}>
            <ApiProvider client={api}>
              <RealtimeProvider connectionFactory={connectionFactory}>{ui}</RealtimeProvider>
            </ApiProvider>
          </MemoryRouter>
        </AuthProvider>
      </ToastProvider>
    </ThemeProvider>
    </PerfProvider>,
  );
  return { ...view, hub, api, oidc };
}
