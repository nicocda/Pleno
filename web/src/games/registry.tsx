import { lazy, type ComponentType, type LazyExoticComponent } from "react";
import type { GameInfo } from "../api/types";

/**
 * Las paginas de juego que conoce el front, por id. Es la contraparte del catalogo del servidor: el servidor dice QUE juegos estan
 * habilitados (nombre, descripcion, ruta) y el front sabe CON QUE pagina se juega cada uno. Un juego puede ser tan simple como una pagina
 * JS que se resuelve sola: alcanza con que cumpla el contrato (una ficha, una pagina, y si mueve fichas, usar la Wallet por el servidor).
 */
export interface GamePage {
  id: string;
  /** Ruta (sin "/" inicial) donde se monta la pagina. Tiene que coincidir con la `route` que declara el servidor. */
  route: string;
  component: LazyExoticComponent<ComponentType>;
  /** Solo para juegos que se resuelven enteros en el navegador y no tienen modulo en el servidor: su propia ficha de catalogo. */
  clientInfo?: GameInfo;
}

// PixiJS pesa bastante: cada pagina se descarga recien cuando el jugador entra a ella.
export const GAME_PAGES: readonly GamePage[] = [
  { id: "roulette", route: "ruleta", component: lazy(() => import("../pages/Roulette").then((m) => ({ default: m.Roulette }))) },
  { id: "slots", route: "tragamonedas", component: lazy(() => import("../pages/Slots").then((m) => ({ default: m.Slots }))) },
  { id: "crash", route: "crash", component: lazy(() => import("../pages/Crash").then((m) => ({ default: m.Crash }))) },
  { id: "blackjack", route: "blackjack", component: lazy(() => import("../pages/Blackjack").then((m) => ({ default: m.Blackjack }))) },
  { id: "uno", route: "uno", component: lazy(() => import("../pages/Uno").then((m) => ({ default: m.Uno }))) },
  { id: "poker", route: "poker", component: lazy(() => import("../pages/Poker").then((m) => ({ default: m.Poker }))) },
  { id: "truco", route: "truco", component: lazy(() => import("../pages/Truco").then((m) => ({ default: m.Truco }))) },
];

/** Lo que viene en el roadmap y todavia no existe: se muestra como "Proximamente" (no depende del servidor). */
export const COMING_SOON: readonly Pick<GameInfo, "id" | "name" | "tagline" | "glyph">[] = [];

export const pageFor = (id: string): GamePage | undefined => GAME_PAGES.find((page) => page.id === id);

/** Los juegos que mostrar en el lobby: los del catalogo del servidor + los que viven solo en el front. */
export function lobbyGames(catalog: readonly GameInfo[]): GameInfo[] {
  const clientOnly = GAME_PAGES.flatMap((page) => (page.clientInfo ? [page.clientInfo] : []));
  return [...catalog, ...clientOnly.filter((info) => !catalog.some((game) => game.id === info.id))];
}
