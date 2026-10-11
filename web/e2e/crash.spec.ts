import { expect, test, type Page } from "@playwright/test";

// Usuario de desarrollo del realm. SOLO desarrollo local. Necesita el sistema levantado (aspire start).
const USER = process.env.E2E_USER ?? "jugador2";
const PASSWORD = process.env.E2E_PASSWORD ?? "jugador2-dev";

async function login(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: /Entrar o crear cuenta/ }).click();
  await page.locator("#username").fill(USER);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#kc-login").click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hola, ${USER}`) })).toBeVisible();
}

test("Crash: una ronda compartida, una apuesta con retiro automatico y la verificacion en el navegador", async ({ page }) => {
  // Las rondas son reales y compartidas: una puede durar hasta ~100 s (el cohete llega a x1.000), asi que se espera con margen.
  test.setTimeout(360_000);
  await login(page);

  // El juego esta en el lobby porque el servidor lo tiene habilitado (el lobby sale del catalogo).
  await page.getByRole("link", { name: /Crash/ }).first().click();
  await expect(page.getByRole("heading", { name: "Retirá antes de que explote" })).toBeVisible();

  // Se espera una ronda con la ventana de apuestas abierta y se apuesta con un retiro automatico bajo (el resultado depende del azar:
  // lo que se comprueba es que la apuesta se resuelve, el saldo cierra y la ronda se puede verificar).
  await expect(page.getByText(/Apuestas abiertas: cierran en/)).toBeVisible({ timeout: 150_000 });
  await page.getByLabel(/Retiro automático/).fill("1,10");
  await page.getByRole("button", { name: /Apostar 10 fichas/ }).click();
  await expect(page.getByText(/Tu apuesta de 10 fichas está en juego|Reservando tus fichas|Apuesta hecha/).first()).toBeVisible({ timeout: 10_000 });

  // El cohete sube y explota (todos los jugadores ven lo mismo).
  await expect(page.getByText("El cohete está subiendo")).toBeVisible({ timeout: 30_000 });
  // Si retiro (ganó), el aviso de victoria aparece mientras el cohete sigue subiendo: se cierra para poder seguir viendo la ronda.
  const dialog = page.getByRole("dialog", { name: "¡Ganaste!" });
  if (await dialog.isVisible().catch(() => false)) await page.getByRole("button", { name: "Continuar" }).click();
  await expect(page.getByText(/¡Explotó en \d/)).toBeVisible({ timeout: 150_000 });
  if (await dialog.isVisible().catch(() => false)) await page.getByRole("button", { name: "Continuar" }).click();

  // La apuesta queda resuelta: ganada (retiro en x1,10) o perdida.
  const bets = page.getByRole("list", { name: "Tus últimas apuestas" });
  await expect(bets.getByText(/\+\d+|perdida/).first()).toBeVisible({ timeout: 30_000 });

  // Verificacion provably fair: el compromiso publicado antes de apostar coincide con la semilla revelada y el punto se recalcula igual.
  await page.getByRole("button", { name: "Verificar en mi navegador" }).click();
  await expect(page.getByText(/La semilla coincide con el compromiso/)).toBeVisible();
  await expect(page.getByText(/El punto recalculado .* coincide/)).toBeVisible();
});
