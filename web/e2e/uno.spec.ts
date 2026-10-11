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

test("Uno: crear una mesa de 2 con un bot, jugar la partida completa y verificar el compromiso en el navegador", async ({ page }) => {
  // La partida es real: los bots juegan cada ~1,2 s y un turno humano dura hasta 30 s, pero aca se juega al instante. Tope de 8 minutos de juego.
  test.setTimeout(11 * 60_000);
  await login(page);

  // El juego esta en el lobby porque el servidor lo tiene habilitado (el lobby sale del catalogo).
  await page.getByRole("link", { name: /Uno/ }).first().click();
  await expect(page.getByRole("heading", { name: "Quedate sin cartas" })).toBeVisible();

  const create = page.getByRole("button", { name: "Crear mesa", exact: true });
  const leaveRoom = page.getByRole("button", { name: "Salir de la mesa", exact: true });
  const hand = page.getByTestId("uno-hand");

  // Si una corrida anterior dejo al jugador sentado, el lobby lo lleva a esa mesa: si sigue abierta se la deja; si ya esta en juego, se sigue jugandola.
  await expect(create.or(leaveRoom).or(hand)).toBeVisible({ timeout: 20_000 });
  if (await leaveRoom.isVisible()) {
    await leaveRoom.click({ timeout: 5_000 });
    await expect(create).toBeVisible();
  }

  if (await create.isVisible()) {
    await page.getByLabel("Entrada (fichas)").fill("10");
    await page.getByRole("radio", { name: "2", exact: true }).click({ timeout: 5_000 });
    await create.click({ timeout: 5_000 });

    // Sala de espera: se suma un bot y se empieza apenas las fichas del jugador esten confirmadas (la reserva es asincrona).
    await expect(page.getByRole("list", { name: "Asientos" })).toBeVisible();
    await page.getByRole("button", { name: "Agregar bot", exact: true }).click({ timeout: 5_000 });
    const start = page.getByRole("button", { name: "Empezar", exact: true });
    await expect(start).toBeEnabled({ timeout: 30_000 });
    await start.click({ timeout: 5_000 });
  }

  // Estrategia simple: jugar la primera carta jugable (los comodines piden color: rojo); si no hay, robar; si la robada se puede jugar, se juega en la
  // proxima vuelta (queda como jugable); si no, pasar. Se termina cuando aparece la verificacion (la semilla se revela al cerrar la partida).
  const verify = page.getByRole("button", { name: "Verificar en mi navegador", exact: true });
  const playable = hand.getByRole("button", { name: /^Jugar / });
  const color = page.getByRole("button", { name: "Color rojo", exact: true });
  const draw = page.getByRole("button", { name: "Robar carta", exact: true });
  const pass = page.getByRole("button", { name: "Pasar", exact: true });

  await expect(hand).toBeVisible({ timeout: 30_000 });
  const deadline = Date.now() + 8 * 60_000;
  while (Date.now() < deadline) {
    if (await verify.isVisible()) break;
    if ((await playable.count()) > 0) {
      await playable.first().click({ timeout: 3_000 }).catch(() => undefined); // si el turno cambio justo, se reintenta en la vuelta siguiente
      if (await color.isVisible()) await color.click({ timeout: 3_000 }).catch(() => undefined);
    } else if (await draw.isEnabled({ timeout: 1_000 }).catch(() => false)) {
      await draw.click({ timeout: 3_000 }).catch(() => undefined);
    } else if (await pass.isVisible()) {
      await pass.click({ timeout: 3_000 }).catch(() => undefined);
    }
    await page.waitForTimeout(500);
  }
  await expect(verify).toBeVisible({ timeout: 5_000 });

  // Si la partida termino con victoria, se cierra el aviso para seguir.
  const dialog = page.getByRole("dialog", { name: "¡Ganaste!" });
  if (await dialog.isVisible()) await page.getByRole("button", { name: "Continuar", exact: true }).click({ timeout: 5_000 });
  await expect(page.getByTestId("uno-result")).toBeVisible();

  // Verificacion provably fair: el hash de la semilla revelada coincide con el compromiso publicado antes de repartir.
  await verify.click({ timeout: 5_000 });
  await expect(page.getByText(/La semilla coincide con el compromiso/)).toBeVisible();
});
