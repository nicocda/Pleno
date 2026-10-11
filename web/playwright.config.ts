import { defineConfig, devices } from "@playwright/test";

// Pruebas de extremo a extremo con un navegador REAL contra el sistema levantado (`aspire start`):
// front en :5173, API, Keycloak en :8080, RabbitMQ, Postgres y Redis. No corren en el CI: necesitan toda la infraestructura.
export default defineConfig({
  testDir: "./e2e",
  timeout: 120_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:5173",
    // El front en modo red local (docs/red-local.md) sirve con un certificado de desarrollo: hay que aceptarlo.
    ignoreHTTPSErrors: true,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  // Escritorio y celular: las pruebas de UX tienen su propio proyecto: mobile.spec.ts (Pixel 7) y desktop.spec.ts (notebook de 1366 x 768).
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] }, testIgnore: /(mobile|desktop)\.spec\.ts/ },
    { name: "mobile", use: { ...devices["Pixel 7"] }, testMatch: /mobile\.spec\.ts/ },
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1366, height: 768 } }, testMatch: /desktop\.spec\.ts/ },
  ],
});
