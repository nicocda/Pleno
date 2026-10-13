import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createContext, useContext, useMemo, type ReactNode } from "react";
import { useAuth } from "../auth/AuthContext";
import { config } from "../config";
import { ApiError, createApiClient, type ApiClient } from "./client";

const ApiContext = createContext<ApiClient | null>(null);
const IdentityApiContext = createContext<ApiClient | null>(null);

function newQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 5_000,
        refetchOnWindowFocus: true,
        // Un 4xx no se arregla reintentando (salvo el caso de la cuenta nueva, que maneja su propio hook).
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
      },
    },
  });
}

/** Cliente HTTP autenticado + cache de datos del servidor. */
export function ApiProvider({ children, client, identityClient }: { children: ReactNode; client?: ApiClient; identityClient?: ApiClient }) {
  const auth = useAuth();
  const queryClient = useMemo(() => newQueryClient(), []);
  const api = useMemo(
    () =>
      client ??
      createApiClient({
        getToken: auth.getAccessToken,
        // Un 401 significa sesion vencida o invalida: se cierra y vuelve la pantalla de entrada, en la misma pagina.
        onUnauthorized: () => auth.expire(),
      }),
    [client, auth],
  );

  // Las pantallas de administracion de cuentas hablan directo con el servicio de identidad (con el mismo token).
  const identityApi = useMemo(
    () => identityClient ?? createApiClient({ getToken: auth.getAccessToken, onUnauthorized: () => auth.expire(), baseUrl: config.identityUrl }),
    [identityClient, auth],
  );

  return (
    <QueryClientProvider client={queryClient}>
      <ApiContext.Provider value={api}>
        <IdentityApiContext.Provider value={identityApi}>{children}</IdentityApiContext.Provider>
      </ApiContext.Provider>
    </QueryClientProvider>
  );
}

export function useApi(): ApiClient {
  const api = useContext(ApiContext);
  if (!api) throw new Error("useApi debe usarse dentro de <ApiProvider>.");
  return api;
}

export function useIdentityApi(): ApiClient {
  const api = useContext(IdentityApiContext);
  if (!api) throw new Error("useIdentityApi debe usarse dentro de <ApiProvider>.");
  return api;
}
