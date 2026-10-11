# Frontend (`web/`)

React 19 + Vite + TypeScript. Decisiones y motivos: [ADR 0005](adr/0005-frontend-react-con-oidc.md).

## Levantarlo
Con todo el sistema (recomendado): `aspire start` levanta Postgres, RabbitMQ, Redis, Keycloak, la API **y el front** en
**http://localhost:5173**. Entrá con `jugador1` / `jugador1-dev` o `jugador2` / `jugador2-dev` (ver [autenticacion.md](autenticacion.md)),
o creá una cuenta nueva desde la pantalla de Keycloak.

Solo el front (necesita la API y Keycloak ya corriendo):

```
cd web
npm install
npm run dev
```

## Comandos
| Comando | Qué hace |
| --- | --- |
| `npm run dev` | Servidor de desarrollo en :5173 (el puerto es fijo: Keycloak solo acepta esa redirección) |
| `npm run build` | Chequeo de tipos y build de producción |
| `npm run lint` | ESLint (incluye las reglas de React hooks) |
| `npm test` | Pruebas unitarias y de componentes (Vitest) |
| `npm run e2e` | Pruebas con un navegador real (Playwright). Requieren `aspire start` activo; no corren en el CI |

La primera vez: `npx playwright install chromium`.

## Configuración
Valores públicos (no hay secretos en el navegador), con defaults para desarrollo:

| Variable | Default |
| --- | --- |
| `VITE_API_URL` | `http://localhost:5188` |
| `VITE_OIDC_AUTHORITY` | `http://localhost:8080/realms/casino` |
| `VITE_OIDC_CLIENT_ID` | `casino-web` |

La API necesita `Cors:AllowedOrigins` con el origen del front (el AppHost ya lo configura).

## Estructura
```
web/src/
  auth/        sesión OIDC (AuthProvider), regreso de Keycloak, puerta de páginas privadas
  api/         cliente HTTP con token, tipos del contrato, hooks de datos
  realtime/    conexión SignalR y saldo en vivo (reductor con versión)
  pages/       Lobby, Ruleta, Historial
  components/  cabecera, saldo animado, avisos, casillero
  lib/         formato, mensajes en español, lógica de la ruleta, enlace de verificación
  theme/       tema claro y oscuro
  test/        utilidades de prueba (sesión, API y conexión simuladas)
web/e2e/       recorridos con navegador real
```

## Qué hay hoy
- Login/logout con Keycloak, saldo en vivo con indicador de conexión, tema claro y oscuro.
- Lobby con catálogo (Ruleta disponible; el resto "Próximamente") y últimas jugadas.
- Mesa de ruleta: tapete completo (los 37 números, caballos, calles, cuadros, seisenas, docenas, columnas y chances simples) y rueda animada con PixiJS. Se puede apostar a varios lugares en la misma tirada (con "Deshacer" y "Quitar todo").
- Tragamonedas de 3 rodillos con tabla de pagos y retorno publicados (`/tragamonedas`); los rodillos frenan en lo que ya decidió el servidor.
- Panel de administración: carga de fichas (`/admin`), historial de cargas (`/admin/cargas`) y ajustes de la tragamonedas (`/admin/tragamonedas`, con prueba en vivo del retorno antes de publicar).
- Blackjack (`/blackjack`): mesas compartidas contra el crupier (cartas con CSS, turno con cuenta regresiva, Pedir/Plantarme) y verificación en el navegador del zapato con la semilla revelada.
- Uno (`/uno`) y mesas entre jugadores: el componente genérico `TablesLobby` (lista de mesas, crear mesa pública o privada, unirse con un click o con el código de 6 caracteres (`POST /tables/join`), sala de espera con bots y "Empezar") es el que van a reutilizar Truco y Poker; Uno aporta el tablero (mano, pila, color en juego, turno con cuenta regresiva, historial) y la verificación del compromiso de la partida. El aviso `GameBroadcast` / `tableChanged` refresca la lista y la mesa de cualquier juego de mesas.
- Poker (`/poker`): Texas Hold'em de una mano por mesa (2 a 6 jugadores o bots) sobre `TablesLobby`: tus 2 cartas, comunitarias, pozo, pila y apuesta de cada jugador (D/SB/BB), cuenta regresiva del turno, Retirarme/Pasar/Igualar y Subir (slider + campo + ½ pozo/pozo/All-in dentro de `minRaiseTo`..`maxRaiseTo`), showdown con las manos reveladas y verificación del compromiso; la lógica de cartas y textos vive en `lib/poker.ts`.
- Movimientos (`/movimientos`): extracto de la cuenta con el saldo después de cada cambio, derivado del ledger. El administrador tiene el historial completo de cargas (`/admin/cargas`) con filtros por jugador y fechas y el total del filtro.
- Historial con verificación: revelar la semilla y abrir la página pública `/verify` ya completa.
- Truco (`/truco`): segunda página sobre `TablesLobby` (2 jugadores o jugador contra bot, a 15 puntos, 45 s por turno): tablero con marcador, tus cartas clickeables solo cuando `actions` incluye `play`, mesa con las cartas jugadas en UNA pila (cada mano se juega encima de la anterior, un poco corrida y desordenada, para que se vean las de abajo) y el resultado de cada mano, la ronda que acaba de terminar a la vista unos segundos, canto pendiente ("Jugador 2 cantó truco: ¿quiero?"), botones de canto agrupados (Envido · Truco · Respuesta, "Al mazo" aparte) que salen de `actions`, tus puntos de envido (solo para vos) y verificación del compromiso. La decodificación de cartas y los textos están en `web/src/lib/truco.ts`.

## Celular
Está pensado para usarse con el pulgar. Lo que se cuida (y se prueba en `web/e2e/mobile.spec.ts`, el proyecto `mobile` de Playwright con la pantalla de un Pixel 7 y una de 360 x 640):
- **Barra superior de una sola fila** (menos de 64 px; antes eran 125): marca, saldo y un botón de menú. La navegación, el tema y el usuario/Salir viven en el menú desplegable (`☰`), que se cierra
  con Escape, con un toque afuera o al navegar. En escritorio la barra no cambia.
- **Botón principal siempre a la vista:** Apostar (ruleta), Girar (tragamonedas), Apostar/Retirar (crash), Sentarme y apostar (blackjack) y, en las mesas de cartas, *tu mano y tus botones*
  (Uno, Truco, Poker) van en una **barra pegada abajo** (`.dock`). En partida desaparece el título de la página y el rótulo de la mesa para que el tablero arranque arriba.
- Las manos de cartas de Uno y Truco son **una fila que se desliza**; Poker pone tus cartas al lado de Retirarme/Pasar y la subida abajo a lo ancho.
- Lobby con los juegos de a dos y cortos (sin la descripción); formularios de mesas a lo ancho; sin scroll horizontal en ninguna pantalla; botones de al menos 44 px de alto.
Los ajustes de celular están en `@media (max-width: 640px)` de `styles.css`; los específicos de cada juego, en un bloque **al final** del archivo (para ganarle a las reglas base de cada juego).
`npm --prefix web run e2e -- --project=mobile` corre solo las pruebas de celular (necesita `aspire start`).

**Escritorio:** las mismas medidas (barra fina, titulo de la pagina fuera en partida, boton principal y mano de cartas pegados abajo con `.dock`, mesas compactas) viven en un bloque `@media (min-width: 641px)` al final de `styles.css`; la navegacion sigue a la vista, sin menu desplegable. Se prueba en `web/e2e/desktop.spec.ts` (proyecto `desktop`, 1366 x 768): `npm --prefix web run e2e -- --project=desktop`. Si el front corre en modo red local (HTTPS), usá `E2E_BASE_URL=https://<tu-ip>:5173`.

### Cartas del Truco
Las cartas del Truco son de la **baraja española** (`web/src/components/SpanishCard.tsx`): espadas, bastos, oros y copas dibujados como tales (espada, garrote con hojas, moneda y cáliz), con los números 1 a 7 acomodados como en el mazo real y la sota (10), el caballo (11) y el rey (12) como personajes. Son SVG propios, sin imágenes ni dependencias, y escalan al tamaño de la carta (66 x 99 px en la mesa y 52 x 78 en el celular). El nombre accesible sigue siendo "7 de espadas".

