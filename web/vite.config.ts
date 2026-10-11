import basicSsl from "@vitejs/plugin-basic-ssl";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Puerto fijo: Keycloak solo acepta redirecciones a http://localhost:5173/* (ver realms/casino-realm.json).
const port = Number(process.env.PORT ?? 5173);

// Modo red local (lo activa el AppHost cuando hay `Lan:Host`, ver docs/red-local.md): el front sirve por HTTPS en 0.0.0.0 y hace de
// unico punto de entrada. El navegador de otro dispositivo solo habla con este origen; Vite reenvia a la API y a Keycloak, que siguen
// escuchando solo en localhost. HTTPS hace falta porque el login (PKCE) usa crypto.subtle, que solo existe en contextos seguros.
const lan = process.env.LAN_MODE === "true";
const apiTarget = process.env.API_TARGET ?? "http://localhost:5188";
const keycloakTarget = process.env.KEYCLOAK_TARGET ?? "http://localhost:8080";

const lanProxy = {
  "/api": { target: apiTarget, changeOrigin: true, ws: true, rewrite: (path: string) => path.replace(/^\/api/, "") },
  // xfwd: Keycloak (KC_PROXY_HEADERS=xforwarded) arma sus URLs con el protocolo y host que ve el navegador.
  "/realms": { target: keycloakTarget, changeOrigin: false, xfwd: true },
  "/resources": { target: keycloakTarget, changeOrigin: false, xfwd: true },
};

export default defineConfig({
  plugins: [react(), ...(lan ? [basicSsl()] : [])],
  // host: true escucha en 0.0.0.0 para abrir el front desde otro dispositivo de la red local.
  server: { host: true, port, strictPort: true, ...(lan ? { proxy: lanProxy } : {}) },
  preview: { port, strictPort: true },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: "./src/test/setup.ts",
    css: false,
    exclude: ["e2e/**", "node_modules/**"],
  },
});
