import basicSsl from "@vitejs/plugin-basic-ssl";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Puerto fijo: el servicio de identidad solo acepta volver a http://localhost:5173 (Identity:AllowedReturnOrigins del AppHost).
const port = Number(process.env.PORT ?? 5173);

// Modo red local (lo activa el AppHost cuando hay `Lan:Host`, ver docs/red-local.md): el front sirve por HTTPS en 0.0.0.0 y hace de
// unico punto de entrada. El navegador de otro dispositivo solo habla con este origen; Vite reenvia a la API y al servicio de identidad, que siguen
// escuchando solo en localhost. HTTPS hace falta para que el navegador acepte las cookies seguras del login con Google.
const lan = process.env.LAN_MODE === "true";
const apiTarget = process.env.API_TARGET ?? "http://localhost:5188";
const identityTarget = process.env.IDENTITY_TARGET ?? "http://localhost:8080";

const lanProxy = {
  "/api": { target: apiTarget, changeOrigin: true, ws: true, rewrite: (path: string) => path.replace(/^\/api/, "") },
  // xfwd: el servicio de identidad (X-Forwarded-*) arma sus URLs con el protocolo y host que ve el navegador.
  "/connect": { target: identityTarget, changeOrigin: false, xfwd: true },
  "/identity": { target: identityTarget, changeOrigin: false, xfwd: true },
  "/signin-google": { target: identityTarget, changeOrigin: false, xfwd: true },
  "/.well-known": { target: identityTarget, changeOrigin: false, xfwd: true },
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
