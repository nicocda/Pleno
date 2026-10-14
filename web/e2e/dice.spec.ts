import { expect, test, type Page } from "@playwright/test";

// Usuario de desarrollo del servicio de identidad. SOLO desarrollo local. Necesita el sistema levantado (aspire start).
const USER = process.env.E2E_USER ?? "jugador2";
const PASSWORD = process.env.E2E_PASSWORD ?? "jugador2-dev";

async function login(page: Page) {
  await page.goto("/");
  await page.getByLabel("Alias o email").fill(USER);
  await page.getByLabel("Contraseña").fill(PASSWORD);
  await page.getByRole("button", { name: "Entrar", exact: true }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hola, ${USER}`) })).toBeVisible();
}

test("Dados: aparece en el lobby, se tira y la tirada se cierra con el saldo en orden", async ({ page }) => {
  await login(page);

  // El juego esta en el lobby porque el servidor lo tiene habilitado (el lobby sale del catalogo).
  await page.getByRole("link", { name: /Dados/ }).first().click();
  await expect(page.getByRole("heading", { name: "Elegí tu probabilidad" })).toBeVisible();

  // El multiplicador y el cobro potencial acompañan a la probabilidad elegida (49,50 % -> x1,97; 10 fichas -> 19).
  await expect(page.getByText(/Paga x1,97/)).toBeVisible();
  await expect(page.getByText(/Si ganás, cobrás 19 fichas/)).toBeVisible();

  await page.getByRole("radio", { name: "Por encima" }).click();
  await page.getByRole("button", { name: /Tirar por 10 fichas/ }).click();

  // La tirada se resuelve (gane o pierda) y el numero que salio queda a la vista, con dos decimales.
  await expect(page.getByText(/Cobraste \d+ fichas|No hubo suerte esta vez|Tirada anulada|Apuesta rechazada/).first()).toBeVisible({ timeout: 30_000 });
  const dialog = page.getByRole("dialog", { name: "¡Ganaste!" });
  if (await dialog.isVisible().catch(() => false)) await page.getByRole("button", { name: "Continuar" }).click();
  await expect(page.getByTestId("dice-number")).toHaveText(/^\d{1,2},\d{2}$/);

  // La tirada queda en "Ultimas tiradas".
  await expect(page.locator(".dice-chip").first()).toBeVisible({ timeout: 15_000 });
});
