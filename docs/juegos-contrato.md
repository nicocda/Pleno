# Juegos: el contrato y como agregar o quitar uno

Decision y motivos: [ADR 0008](adr/0008-juegos-como-modulos-sobre-un-contrato.md).

La idea: **todo juego cumple un contrato base y despues se implementa como quiera**. La ruleta usa un RNG provably fair y una tabla de
apuestas, la tragamonedas una tabla de pagos versionada, y un juego futuro podria ser una ronda en vivo compartida (Crash) o incluso una
pagina JS que se resuelve sola. A la plataforma no le importa como esta hecho por dentro; le importa que cumpla esto.

## Lo que TODO juego declara (el contrato)
1. **Una ficha de catalogo** (`GameInfo`): `id` estable (minusculas y guiones), nombre, descripcion corta, ruta del front, simbolo y
   `resolution` (quien decide el resultado: `Server`, verificable, o `Client`, una pagina que se basta sola). El servidor la publica en
   `GET /games` y **el lobby se arma con eso**: no hay una lista fija en el front.
2. **Identidad del token, nunca del cuerpo.** Todos sus endpoints exigen autenticacion y el jugador sale del `sub` del token.
3. **Fichas como enteros** (`long`) y **`Idempotency-Key`** en toda operacion que mueve fichas.
4. **Si mueve fichas, el protocolo de rondas con la Wallet** (`IGameRounds`): el juego pide reservar (`ReserveStake`) → la Wallet reserva
   (`StakeReserved`) → el juego resuelve y avisa el premio TOTAL (`RoundResolved`) → la Wallet liquida (`StakeSettled`) → el juego cierra la
   ronda y publica `RoundClosed`. Si la Wallet rechaza o la reserva vence, la ronda se cierra rechazada o anulada. Todos los pasos son
   idempotentes (la entrega es "al menos una vez"); los hechos de la Wallet solo traen el `BetId` y cada juego ignora los que no son suyos.
5. **Sus endpoints bajo su propia ruta**: `/games/{id}/...` (jugador) y `/backoffice/games/{id}/...` (administracion).
6. **Su propio almacenamiento** en la base del servicio de juegos (`gamesdb`): cada juego registra sus tipos de documentos y eventos.

En codigo: `IGameModule` (ficha, servicios, almacenamiento, endpoints) e `IGameRounds` (el protocolo), en `Casino.Modules.Games/Platform`.
Las pruebas de contrato (`GameContractTests`) recorren la lista de juegos y verifican las convenciones (ficha valida, `IGameRounds`
registrado, rutas propias, endpoints autenticados): **todo juego nuevo queda verificado solo**.

## Lo que es libre
Las reglas, las tablas, el motor, la forma de resolver una ronda, la arquitectura interna del modulo, el RNG (o no usar ninguno), y la
interfaz: una pagina React con PixiJS, HTML y CSS, o una pagina JS que se resuelve sola. Mientras cumpla el contrato, esta bien.

## Agregar un juego
1. Crear el proyecto `src/Modules/Games.{Nombre}` (referencia `Casino.Modules.Games`) con su `IGameModule` (y su `IGameRounds` si mueve fichas).
2. Agregarlo a `AvailableGames.All()` en `Casino.GamesService`. Es la unica lista: las pruebas de contrato lo recorren solas.
3. Registrar su pagina en `web/src/games/registry.tsx` (id, ruta y componente). El lobby y las rutas del front salen de ahi y del catalogo.
4. Escribir sus pruebas propias (las reglas del juego) y, si mueve fichas, la de saldo cerrado.

## Quitar un juego
- **Sin recompilar:** dejarlo afuera con `Games:Enabled` (por ejemplo `"Games": { "Enabled": ["roulette"] }`). Desaparece del catalogo, del lobby y
  sus rutas dejan de existir. Un id desconocido en esa lista impide arrancar (para no dejar nada "a medias").
- **Del todo:** quitarlo de `AvailableGames.All()` (y su pagina del registro del front).

## Juegos en vivo (ejemplo: Crash)
Un juego de ronda compartida (Crash) usa lo mismo y suma dos piezas de la plataforma: la **difusion a todos los jugadores** (`GameBroadcast`, por RabbitMQ y SignalR) y un
**plazo propio para la reserva** de la Wallet (`ReserveStake.TtlSeconds`) cuando la ronda dura mas que el plazo por defecto. Su motor de rondas es un servicio en segundo
plano registrado por el propio modulo. Ver `docs/juego-crash.md`.

## Juegos de mesa (ejemplo: Blackjack)
Un juego de mesas (Blackjack) es el mismo contrato con varias partidas simultaneas: cada mesa tiene su mano compartida, turnos y cartas ocultas, y el modulo trae un
motor con un ciclo por mesa. Cada apuesta (cada asiento) sigue el protocolo de rondas con la Wallet por su cuenta. El aviso en vivo (`GameBroadcast`) solo dice "la mesa
cambio": el navegador vuelve a pedir el estado, que arma el servidor sin la carta tapada. Ver `docs/juego-blackjack.md` y el ADR 0010.

## Juegos de mesas entre jugadores (ejemplo: Uno)
Uno, Truco y Poker se escriben como una maquina de estados pura (`ITableGame`) sobre la plataforma `Games.Tables`, que pone las mesas, los bots, los turnos, las fichas y los endpoints.
Ver `docs/juegos-mesas.md` y el ADR 0012.

## Un juego en su propio proceso
El servicio de juegos es un solo programa que carga los modulos habilitados. Si un juego necesita su propio proceso (por ejemplo uno en vivo
y compartido), se levanta el mismo programa con `Games:Enabled` = ese juego y se enruta por el gateway. Cada despliegue tiene su `gamesdb`.

## Juegos que se resuelven en la pagina (`resolution: Client`)
La ficha del catalogo acepta `Client` y el registro del front permite un juego **sin modulo en el servidor** (con su `clientInfo`): sirve para
juegos de practica, sin fichas. **Todavia no hay un endpoint generico de rondas** para que una pagina mueva fichas por la Wallet: un juego
que apuesta fichas necesita su modulo en el servidor (la pagina no puede ser la que decide un premio sin que el casino lo pueda verificar o acotar).
Si hace falta, se agrega despues como un modulo generico con topes de premio.
