// Configuracion publica del front. Nada de esto es secreto: el navegador es un cliente "publico" del servicio de identidad
// y no guarda ningun secreto. Los valores por defecto sirven para `aspire run`.
export const config = {
  apiUrl: (import.meta.env.VITE_API_URL ?? "http://localhost:5188").replace(/\/$/, ""),
  identityUrl: (import.meta.env.VITE_IDENTITY_URL ?? "http://localhost:8080").replace(/\/$/, ""),
  oidcClientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? "casino-web",
} as const;
