import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import type { AuthClient, AuthUser } from "./identity";

export type AuthStatus = "loading" | "authenticated" | "anonymous";

export interface AuthApi {
  status: AuthStatus;
  user: AuthUser | null;
  /** Nombre para mostrar: es el alias, sale del token y nunca se guarda en nuestra base. */
  displayName: string | null;
  /** El cliente del servicio de identidad, para lo que no es sesion (registro, verificar email, recuperar clave). */
  client: AuthClient;
  /** Entra con alias o email y contraseña. Lanza `AuthError` si no se puede. */
  signIn: (login: string, password: string) => Promise<void>;
  /** Cambia el codigo que dejo el login con Google por una sesion. */
  signInWithCode: (code: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Token vigente o null. Lo usan el cliente HTTP y SignalR en cada llamada, asi siempre es el mas reciente. */
  getAccessToken: () => Promise<string | null>;
  /** El servidor dijo que la sesion no vale (401): se cierra y la pantalla de entrada vuelve sola, en la misma pagina. */
  expire: () => void;
}

const AuthContext = createContext<AuthApi | null>(null);

export function AuthProvider({ client, children }: { client: AuthClient; children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [status, setStatus] = useState<AuthStatus>("loading");

  useEffect(() => {
    let active = true;
    const onLoaded = (loaded: AuthUser) => {
      if (!active) return;
      setUser(loaded);
      setStatus("authenticated");
    };
    const onUnloaded = () => {
      if (!active) return;
      setUser(null);
      setStatus("anonymous");
    };

    client.events.addUserLoaded(onLoaded);
    client.events.addUserUnloaded(onUnloaded);
    client
      .getUser()
      .then((existing) => {
        if (!active) return;
        if (existing && !existing.expired) onLoaded(existing);
        else onUnloaded();
      })
      .catch(onUnloaded);

    return () => {
      active = false;
      client.events.removeUserLoaded(onLoaded);
      client.events.removeUserUnloaded(onUnloaded);
    };
  }, [client]);

  const signIn = useCallback(async (login: string, password: string) => void (await client.signIn(login, password)), [client]);
  const signInWithCode = useCallback(async (code: string) => void (await client.signInWithCode(code)), [client]);
  const logout = useCallback(() => client.signOut(), [client]);
  const expire = useCallback(() => void client.signOut(), [client]);
  const getAccessToken = useCallback(() => client.getAccessToken(), [client]);

  const value = useMemo<AuthApi>(
    () => ({ status, user, displayName: user?.profile.preferred_username ?? null, client, signIn, signInWithCode, logout, getAccessToken, expire }),
    [status, user, client, signIn, signInWithCode, logout, getAccessToken, expire],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthApi {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth debe usarse dentro de <AuthProvider>.");
  return context;
}
