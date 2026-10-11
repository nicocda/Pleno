import { expect, test, type Page } from "@playwright/test";

// UX en escritorio (notebook de 1366 x 768). Necesita el sistema levantado (aspire start). Se comprueba que se pueda jugar sin scrollear:
// barra fina con la navegacion a la vista, y el boton principal de cada juego (apostar, girar, la mano de cartas y sus botones) dentro de la pantalla.
const USER = process.env.E2E_USER ?? "jugador2";
const PASSWORD = process.env.E2E_PASSWORD ?? "jugador2-dev";
const MAX_HEADER = 56;
const fully = { ratio: 0.99 };

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
/** Cuantos pixeles se pueden scrollear hacia abajo en la pagina (0 = todo entra en la pantalla). */
const scrollable = (page: Page) => page.evaluate<number>("document.documentElement.scrollHeight - window.innerHeight");

test("escritorio: la barra es fina, con la navegacion y el saldo a la vista y sin menu desplegable", async ({ page }) => {
  await login(page);

  expect(await headerHeight(page)).toBeLessThanOrEqual(MAX_HEADER);
  await expect(page.getByTestId("balance-value").first()).toBeInViewport(fully);
  await expect(page.getByRole("button", { name: "Abrir el menú" })).toBeHidden();
  for (const name of ["Lobby", "Ruleta", "Historial", "Movimientos"]) await expect(page.getByRole("link", { name, exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Salir" })).toBeVisible();
});

test("escritorio: ninguna pantalla tiene scroll horizontal y la barra no crece", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  for (const route of ["/", "/ruleta", "/tragamonedas", "/crash", "/blackjack", "/uno", "/truco", "/poker", "/historial", "/movimientos"]) {
    await page.goto(route);
    await page.waitForLoadState("networkidle").catch(() => undefined);
    expect(await noHorizontalScroll(page), `scroll horizontal en ${route}`).toBe(true);
    expect(await headerHeight(page), `header de ${route}`).toBeLessThanOrEqual(MAX_HEADER);
  }
});

test("escritorio: el lobby muestra todos los juegos sin scrollear", async ({ page }) => {
  await login(page);

  await expect(page.getByRole("link", { name: /Poker/ })).toBeInViewport(fully);
});

test("escritorio: el boton principal de cada juego de apuestas esta a la vista sin scrollear", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  await page.goto("/ruleta");
  await expect(page.getByRole("button", { name: /^Apostar/ })).toBeInViewport(fully);

  await page.goto("/tragamonedas");
  await expect(page.getByRole("button", { name: /^Girar por/ })).toBeInViewport(fully);

  await page.goto("/crash");
  await expect(page.getByRole("button", { name: /^(Apostar|Apuesta hecha|Retirar)/ })).toBeInViewport(fully);

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

test("escritorio: en una partida de Uno la mano esta a la vista y el titulo de la pagina no tapa la mesa", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "uno", "2", "10", "uno-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  await expect(page.getByRole("heading", { name: "Quedate sin cartas" })).toBeHidden();
  expect(await noHorizontalScroll(page)).toBe(true);
});

test("escritorio: en una partida de Truco las cartas y los cantos estan a la vista", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "truco", null, "10", "tru-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  expect(await noHorizontalScroll(page)).toBe(true);
});

test("escritorio: en una mano de Poker mis cartas y las acciones estan a la vista sin scrollear", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  const hand = await startTable(page, "poker", "2", "100", "pk-hand");

  await expect(hand).toBeInViewport({ ratio: 0.8 });
  const fold = page.getByRole("button", { name: "Retirarme", exact: true });
  await expect(fold).toBeVisible({ timeout: 30_000 });
  await expect(fold).toBeInViewport(fully);
  await expect(page.getByRole("button", { name: /^(Subir a|All-in \d)/ })).toBeInViewport(fully);
});

test("escritorio: el boton principal queda pegado abajo de la pantalla aunque se scrollee la pagina", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);

  for (const [route, name] of [["/ruleta", /^Apostar/], ["/tragamonedas", /^Girar por/], ["/crash", /^(Apostar|Apuesta hecha|Retirar)/]] as const) {
    await page.goto(route);
    await page.waitForLoadState("networkidle").catch(() => undefined);
    const button = page.getByRole("button", { name });
    for (const to of [0, 100_000]) {
      await page.evaluate(`window.scrollTo(0, ${to})`);
      await expect(button, `${route} con scroll ${to}`).toBeInViewport(fully);
    }
    expect(await scrollable(page), `${route}: la pagina no debe ser mucho mas larga que la pantalla`).toBeLessThan(400);
  }
});
