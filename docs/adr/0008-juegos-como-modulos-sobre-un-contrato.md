# ADR 0008 - Juegos como modulos sobre un contrato, en su propio servicio

- **Estado:** aceptada
- **Fecha:** 2026-10-09

## Contexto
Despues de separar la Wallet (ADR 0007) faltaba el motor de juegos, y la idea es sumar juegos muy distintos entre si (ruleta y tragamonedas de una
sola tirada hoy; Crash en vivo, blackjack con decisiones en varios pasos mas adelante) y poder agregar o quitar uno de forma sencilla. Un solo
bloque "Games" con todo adentro no escala a eso: cada juego tiene su propia arquitectura.

## Decision
- **Servicio de juegos propio** (`Casino.GamesService`) con **su propia base** (`gamesdb`). `Casino.Api` queda como gateway (usuarios, tiempo real
  y proxy YARP): reenvia `/games`, `/fairness` y `/backoffice/games` a este servicio. Cada servicio valida el token por su cuenta.
- **Cada juego es un modulo propio** (`Casino.Modules.Games.Roulette`, `...Slots`) que implementa un **contrato base** (`IGameModule` +
  `IGameRounds`): ficha de catalogo, servicios y almacenamiento propios, endpoints bajo su ruta y, si mueve fichas, el protocolo de rondas con la
  Wallet. Como lo implemente por dentro es libre. Detalle en `docs/juegos-contrato.md`.
- **El nucleo** (`Casino.Modules.Games`) conserva lo comun: la equidad provably fair (seeds y nonces, compartida por todos los juegos), el catalogo
  (`GET /games`), el reparto de los hechos de la Wallet a los juegos y los tipos comunes (estados de ronda, errores).
- **Activar o desactivar por configuracion:** el servicio carga los juegos de `AvailableGames.All()` filtrados por `Games:Enabled` (todos si esta
  vacio). Un id desconocido impide arrancar. Quitar un juego sin recompilar es dejarlo afuera de esa lista; sumar uno es agregar su proyecto y una linea.
- **El lobby sale del catalogo del servidor** y las paginas se registran por id en el front (`games/registry.tsx`): agregar o quitar un juego no
  toca el lobby ni las rutas. Un juego puede ser una pagina JS que se resuelve sola (`resolution: Client`) mientras cumpla el contrato.
- **Pruebas de contrato** que recorren la lista de juegos (ficha valida, `IGameRounds` registrado, rutas propias, endpoints autenticados): un juego
  nuevo queda verificado sin escribir tests de convenciones.
- **Un juego en su propio proceso, mas adelante:** es el mismo programa con `Games:Enabled` = ese juego y una ruta en el gateway. No se hizo un
  servicio por juego desde ya: obligaria a replicar la equidad entre servicios y multiplicar procesos, bases y pruebas, sin beneficio en una sola maquina.
- **Secreto:** la clave maestra de las semillas (`Fairness:MasterKey`) pasa a ser un secreto de usuario de `Casino.GamesService` (la API ya no la usa).

## Consecuencias
- Agregar Crash (o cualquier juego) es sumar un proyecto, una linea y una pagina; no se toca el gateway, el lobby ni el nucleo.
- Si la Wallet o los juegos caen, el gateway responde error solo en lo que depende de ellos; el resto sigue (hay pruebas).
- Las semillas y los nonces viven en `gamesdb`: si algun dia un juego se despliega aparte, tendra su propio estado de equidad.
- Los datos locales anteriores no se migran (bases nuevas); Keycloak conserva los usuarios.
- Pendiente: un modulo generico para juegos que mueven fichas desde una pagina (`resolution: Client`) con topes de premio; hoy esos juegos son de practica.
