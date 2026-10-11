import { config } from "../config";

/** Error de la API. Las respuestas de error siguen "problem details": `title` es el codigo estable (por ejemplo InsufficientFunds). */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;

  constructor(status: number, title: string, detail?: string) {
    super(detail ?? title);
    this.name = "ApiError";
    this.status = status;
    this.title = title;
  }
}

export interface ApiClient {
  get<T>(path: string): Promise<T>;
  post<T>(path: string, body?: unknown, headers?: Record<string, string>): Promise<T>;
  put<T>(path: string, body?: unknown, headers?: Record<string, string>): Promise<T>;
  delete<T>(path: string): Promise<T>;
}

export interface ApiClientOptions {
  getToken: () => Promise<string | null>;
  /** Se llama ante un 401: el token no sirve (vencio o no hay sesion). */
  onUnauthorized: () => void;
  baseUrl?: string;
  fetchImpl?: typeof fetch;
}

export function createApiClient({ getToken, onUnauthorized, baseUrl = config.apiUrl, fetchImpl }: ApiClientOptions): ApiClient {
  // `fetch` se resuelve en cada llamada (no al crear el cliente) para poder sustituirlo en pruebas.
  const doFetch: typeof fetch = (input, init) => (fetchImpl ?? fetch)(input, init);

  async function request<T>(method: "GET" | "POST" | "PUT" | "DELETE", path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
    const token = await getToken();
    const headers: Record<string, string> = { Accept: "application/json", ...extraHeaders };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers["Content-Type"] = "application/json";

    let response: Response;
    try {
      response = await doFetch(`${baseUrl}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    } catch {
      throw new ApiError(0, "NetworkError", "No se pudo conectar con el servidor.");
    }

    if (response.status === 401) {
      onUnauthorized();
      throw new ApiError(401, "Unauthorized", "Tu sesión venció.");
    }

    if (!response.ok) throw await toApiError(response);
    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  return {
    get: <T>(path: string) => request<T>("GET", path),
    post: <T>(path: string, body?: unknown, headers?: Record<string, string>) => request<T>("POST", path, body, headers),
    put: <T>(path: string, body?: unknown, headers?: Record<string, string>) => request<T>("PUT", path, body, headers),
    delete: <T>(path: string) => request<T>("DELETE", path),
  };
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string };
    return new ApiError(response.status, problem.title ?? `HTTP ${response.status}`, problem.detail);
  } catch {
    return new ApiError(response.status, `HTTP ${response.status}`);
  }
}
