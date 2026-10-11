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

test("Blackjack: sentarse en una mesa, jugar una mano completa y verificarla en el navegador", async ({ page }) => {
  // Las manos son reales y compartidas: hay que esperar a que abra la ventana de apuestas, jugar el turno (tope de 15 s) y que el crupier termine.
  test.setTimeout(240_000);
  await login(page);

  // El juego esta en el lobby porque el servidor lo tiene habilitado (el lobby sale del catalogo).
  await page.getByRole("link", { name: /Blackjack/ }).first().click();
  await expect(page.getByRole("heading", { name: "Ganale al crupier" })).toBeVisible();
  await page.getByRole("button", { name: /Mesa Principiantes/ }).click();

  // Se apuesta en cuanto la mesa tenga una mano abierta para apostar (si esta repartiendo, el boton se habilita en la proxima).
  const sitDown = page.getByRole("button", { name: "Sentarme y apostar" });
  await expect(sitDown).toBeEnabled({ timeout: 60_000 });
  await page.getByRole("radio", { name: "10", exact: true }).click();
  await sitDown.click();
  await expect(page.getByText(/Tu apuesta de 10 fichas está en la mesa|Tenés 10 fichas en juego|Es tu turno/).first()).toBeVisible({ timeout: 15_000 });

  // Se juega el turno: pedir mientras el total sea menor a 17 y plantarse si no. La mano termina cuando aparece la verificacion de la ultima mano
  // (el cartel "Mano terminada" dura solo la pausa entre manos, asi que no se depende de verlo).
  const verify = page.getByRole("button", { name: "Verificar en mi navegador" });
  const mine = page.getByTestId("bj-seat-mine");
  const hit = page.getByRole("button", { name: "Pedir" });
  const stand = page.getByRole("button", { name: "Plantarme" });
  for (let step = 0; step < 12; step += 1) {
    await expect(hit.or(verify)).toBeVisible({ timeout: 90_000 });
    if (await verify.isVisible()) break;
    const text = await mine.locator(".bj-total").innerText({ timeout: 5_000 }).catch(() => ""); // si la mano justo termino, el asiento ya no esta
    const total = Number((text.match(/^(\d+)/) ?? [])[1] ?? 99); // "16 · 10 fichas", "17 blando · ..." o "Blackjack · ..."
    const button = total < 17 ? hit : stand;
    await button.click({ timeout: 5_000 }).catch(() => undefined); // si el turno vencio justo, la mano se planta sola
    await expect(button.or(verify).or(page.getByText(/Juega el crupier|Juega el asiento/))).toBeVisible({ timeout: 30_000 });
  }

  // Si la mano termino con victoria, se cierra el aviso para seguir.
  const dialog = page.getByRole("dialog", { name: "¡Ganaste!" });
  if (await dialog.isVisible().catch(() => false)) await page.getByRole("button", { name: "Continuar" }).click();

  // Verificacion provably fair: el compromiso publicado antes de repartir coincide con la semilla revelada y el reparto sale del mazo.
  await verify.click();
  await expect(page.getByText(/La semilla coincide con el compromiso/)).toBeVisible();
  await expect(page.getByText(/Las cartas del reparto coinciden con el mazo recalculado/)).toBeVisible();
});
