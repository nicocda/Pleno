import { devices, expect, test, type Locator, type Page } from "@playwright/test";

// UX en celular (Pixel 7: 412 x 839). Necesita el sistema levantado (aspire start). Se comprueba lo que se noto a mano: que el header no ocupe media pantalla, que no haya
// scroll horizontal y que lo que se usa todo el tiempo (apostar, girar, la mano de cartas y sus botones) este a la vista sin tener que scrollear.
const USER = process.env.E2E_USER ?? "jugador2";
const PASSWORD = process.env.E2E_PASSWORD ?? "jugador2-dev";

test.use({ ...devices["Pixel 7"] });

async function login(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: /Entrar o crear cuenta/ }).click();
  await page.locator("#username").fill(USER);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#kc-login").click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hola, ${USER}`) })).toBeVisible();
}

const noHorizontalScroll = (page: Page) => page.evaluate<boolean>("document.documentElement.scrollWidth <= window.innerWidth");
const headerHeight = (page: Page) => page.evaluate<number>("document.querySelector('header')?.getBoundingClientRect().height ?? 0");
const fully = { ratio: 0.99 };

/** Un boton que se toca con el dedo: al menos 44 px de alto (la guia de accesibilidad para pantallas tactiles). */
async function expectTappable(button: Locator) {
  const box = await button.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
}

test("celular: el encabezado es una fila fina y la navegacion esta en el menu", async ({ page }) => {
  await login(page);

  expect(await headerHeight(page)).toBeLessThanOrEqual(64); // antes eran 125 px (15 % de la pantalla)
  await expect(page.getByTestId("balance-value").first()).toBeInViewport(fully); // el saldo siempre a la vista
  const menu = page.getByRole("button", { name: "Abrir el menú" });
  await expect(menu).toHaveAttribute("aria-expanded", "false");
  await expect(page.getByRole("link", { name: "Historial" })).toBeHidden();

  await menu.click();
  await expect(page.getByRole("button", { name: "Cerrar el menú" })).toHaveAttribute("aria-expanded", "true");
  await expect(page.getByRole("link", { name: "Movimientos" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Salir" })).toBeVisible();
  await expectTappable(page.getByRole("link", { name: "Historial" }));

  // Se cierra al navegar y con Escape.
  await page.getByRole("link", { name: "Historial" }).click();
  await expect(page).toHaveURL(/\/historial$/);
  await expect(page.getByRole("link", { name: "Historial" })).toBeHidden();
  await page.getByRole("button", { name: "Abrir el menú" }).click();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("link", { name: "Historial" })).toBeHidden();
});

test("celular: ninguna pantalla tiene scroll horizontal y el lobby muestra todos los juegos de un vistazo", async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  expect(await noHorizontalScroll(page)).toBe(true);
  await login(page);

  // El lobby: los juegos van de a dos y cortos, asi que Poker (el ultimo) entra en la primera pantalla.
  await expect(page.getByRole("link", { name: /Poker/ })).toBeInViewport();
  for (const route of ["/", "/ruleta", "/tragamonedas", "/crash", "/blackjack", "/uno", "/truco", "/poker", "/historial", "/movimientos"]) {
    await page.goto(route);
    await page.waitForLoadState("networkidle").catch(() => undefined);
    expect(await noHorizontalScroll(page), `scroll horizontal en ${route}`).toBe(true);
    expect(await headerHeight(page), `header de ${route}`).toBeLessThanOrEqual(64);
  }
});

test("celular: el boton principal de cada juego de apuestas esta a la vista sin scrollear", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  await page.goto("/ruleta");
  await expect(page.getByRole("button", { name: /^Apostar/ })).toBeInViewport(fully);
  await expectTappable(page.getByRole("button", { name: /^Apostar/ }));

  await page.goto("/tragamonedas");
  const spin = page.getByRole("button", { name: /^Girar por/ });
  await expect(spin).toBeInViewport(fully);
  await expectTappable(spin);

  await page.goto("/crash");
  const bet = page.getByRole("button", { name: /^(Apostar|Apuesta hecha|Retirar)/ });
  await expect(bet).toBeInViewport(fully);
  await expectTappable(bet);

  await page.goto("/blackjack");
  await page.getByRole("button", { name: /Mesa Principiantes/ }).click();
  const sit = page.getByRole("button", { name: "Sentarme y apostar" });
  if (await sit.isVisible({ timeout: 10_000 }).catch(() => false)) await expect(sit).toBeInViewport(fully);
});

/** Crea una mesa con un bot (o retoma la partida que ya estuviera en curso) y devuelve el tablero cuando esta a la vista. */
async function startTable(page: Page, game: string, seats: string | null, buyIn: string, board: string) {
  await page.goto(`/${game}`);
  const create = page.getByRole("button", { name: "Crear mesa", exact: true });
  const leave = page.getByRole("button", { name: "Salir de la mesa", exact: true });
  const hand = page.getByTestId(board);
  await expect(create.or(leave).or(hand)).toBeVisible({ timeout: 20_000 });
  if (await leave.isVisible()) {
    await leave.click({ timeout: 5_000 });
    await expect(create).toBeVisible();
  }
  if (await create.isVisible()) {
    await page.getByLabel("Entrada (fichas)").fill(buyIn);
    if (seats) await page.getByRole("radio", { name: seats, exact: true }).click({ timeout: 5_000 });
    await create.click({ timeout: 5_000 });
    await expect(page.getByRole("list", { name: "Asientos" })).toBeVisible();
    await page.getByRole("button", { name: "Agregar bot", exact: true }).click({ timeout: 5_000 });
    const start = page.getByRole("button", { name: "Empezar", exact: true });
    await expect(start).toBeEnabled({ timeout: 30_000 });
    await start.click({ timeout: 5_000 });
  }
  await expect(hand).toBeVisible({ timeout: 30_000 });
  return hand;
}

test("celular: en una partida de Uno la mano esta a la vista y el titulo de la pagina no tapa la mesa", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "uno", "2", "10", "uno-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  await expect(page.getByRole("heading", { name: "Quedate sin cartas" })).toBeHidden(); // en partida, el titulo de la pagina no hace falta
  expect(await noHorizontalScroll(page)).toBe(true);
});

test("celular: en una partida de Truco las cartas y los cantos estan a la vista", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "truco", null, "10", "tru-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  expect(await noHorizontalScroll(page)).toBe(true);
});

test("celular: en una mano de Poker mis cartas y las acciones estan a la vista sin scrollear", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "poker", "2", "100", "pk-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  const fold = page.getByRole("button", { name: "Retirarme", exact: true });
  await expect(fold).toBeVisible({ timeout: 30_000 }); // cuando le toca al jugador
  await expect(fold).toBeInViewport(fully);
  await expect(page.getByRole("button", { name: /^(Subir a|All-in \d)/ })).toBeInViewport(fully); // tambien la subida, hasta abajo de la barra
  await expectTappable(fold);
});

test.describe("celular chico (360 x 640)", () => {
  test.use({ viewport: { width: 360, height: 640 } });

  test("el encabezado sigue siendo una fila, no hay scroll horizontal y se puede apostar sin scrollear", async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);

    expect(await headerHeight(page)).toBeLessThanOrEqual(64);
    expect(await noHorizontalScroll(page)).toBe(true);
    for (const route of ["/ruleta", "/crash", "/poker"]) {
      await page.goto(route);
      await page.waitForLoadState("networkidle").catch(() => undefined);
      expect(await noHorizontalScroll(page), `scroll horizontal en ${route}`).toBe(true);
    }
    await page.goto("/ruleta");
    await expect(page.getByRole("button", { name: /^Apostar/ })).toBeInViewport(fully);
  });
});
