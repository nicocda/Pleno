import { expect, test, type Page } from "@playwright/test";

// Usuarios de desarrollo del realm (src/Casino.AppHost/realms/casino-realm.json). SOLO desarrollo local.
const USER = process.env.E2E_USER ?? "jugador2";
const PASSWORD = process.env.E2E_PASSWORD ?? "jugador2-dev";

async function login(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: /Entrar o crear cuenta/ }).click();
  // Pantalla de Keycloak (no es nuestra): las contraseñas nunca pasan por la aplicacion.
  await page.locator("#username").fill(USER);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#kc-login").click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hola, ${USER}`) })).toBeVisible();
}

const readBalance = async (page: Page) => Number((await page.getByTestId("balance-value").first().innerText()).replace(/\./g, ""));

test.describe.configure({ mode: "serial" });

test("sin sesion se ve la pantalla de entrada y no hay datos privados", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByRole("button", { name: /Entrar o crear cuenta/ })).toBeVisible();
  await expect(page.getByTestId("balance-value")).toHaveCount(0);
});

test("un jugador entra, apuesta, ve el resultado en vivo y lo verifica", async ({ page }) => {
  await login(page);

  // El canal en vivo (SignalR por WebSocket, con el token en la query y pasando por CORS) esta conectado.
  await expect(page.getByText("En vivo")).toBeVisible();
  await expect(page.getByTestId("balance-value").first()).not.toHaveText("—");
  const before = await readBalance(page);
  expect(before).toBeGreaterThan(10);

  // Ruleta: una apuesta al rojo, tocando el tapete.
  await page.getByRole("link", { name: "Ruleta" }).first().click();
  await page.getByRole("radio", { name: "10", exact: true }).click();
  await page.getByRole("button", { name: "Rojo", exact: true }).click();
  await page.getByRole("button", { name: /Apostar 10 fichas/ }).click();

  // La rueda gira y el saldo y el resultado NO se adelantan: se revelan recien cuando la bola cae.
  const wheel = page.getByTestId("wheel");
  await expect(wheel).toHaveAttribute("data-state", /spinning|landing/);
  expect(await readBalance(page)).toBe(before);
  await expect(page.getByText(/Salió el \d+/)).toHaveCount(0);

  await expect(wheel).toHaveAttribute("data-state", "settled", { timeout: 30_000 });
  await expect(page.getByText(/Salió el \d+/).first()).toBeVisible();
  const won = await page.getByText(/Ganaste/).count();

  // El saldo en vivo se movió sin recargar la página: -10 si perdió, +10 neto si gano (paga x2 e incluye la apuesta).
  await expect.poll(() => readBalance(page), { timeout: 15_000 }).toBe(won > 0 ? before + 10 : before - 10);

  // Si gano, el aviso de victoria tapa la pagina: se cierra para seguir (antes el test fallaba la mitad de las veces, justo cuando ganaba).
  const dialog = page.getByRole("dialog", { name: "¡Ganaste!" });
  if (await dialog.isVisible().catch(() => false)) await page.getByRole("button", { name: "Continuar", exact: true }).click({ timeout: 5_000 });

  // Historial: la jugada aparece cobrada.
  await page.getByRole("link", { name: "Historial" }).click();
  await expect(page.getByText("Cobrada").first()).toBeVisible();

  // Revelar la semilla y verificar la jugada con la página pública, que recalcula todo en el navegador.
  await page.getByRole("button", { name: /Revelar semilla y verificar/ }).click();
  await page.getByRole("button", { name: /Sí, revelar/ }).click();
  const link = page.getByRole("link", { name: "Verificar" }).first();
  await expect(link).toBeVisible();
  const popupPromise = page.waitForEvent("popup");
  await link.click();
  const popup = await popupPromise;
  await expect(popup.getByText("La jugada es legítima")).toBeVisible();
  await popup.close();
});

test("cerrar sesion devuelve a la pantalla de entrada", async ({ page }) => {
  await login(page);

  await page.getByRole("button", { name: "Salir" }).click();

  await expect(page.getByRole("button", { name: /Entrar o crear cuenta/ })).toBeVisible();
});
