# ADR 0012 - Plataforma de mesas entre jugadores

- **Estado:** aceptada
- **Fecha:** 2026-10-09

## Contexto
Blackjack (ADR 0011) es de mesas fijas contra la casa. Uno, Truco y Poker se juegan **entre jugadores**: las mesas las crean ellos (publicas o con codigo), cada partida
tiene turnos, informacion oculta (la mano de cada uno) y un pozo que se lleva el ganador. Los tres comparten casi todo lo que no son las reglas: mesas, asientos, fichas,
turnos con tiempo limite, ausentes, semilla provably fair, tiempo real. Escribir eso tres veces seria repetir los errores tres veces.

## Decision
- **Una plataforma, muchos juegos.** El proyecto `Casino.Modules.Games.Tables` pone todo lo comun y cada juego es un modulo chico que implementa **`ITableGame`**: una
  maquina de estados **pura** (sin base de datos, sin reloj propio, sin saber de la Wallet ni de quien es cada jugador: solo asientos `0..n-1`). El estado viaja como JSON,
  asi cada juego se prueba sin infraestructura y con partidas completas de bots contra bots. Un juego nuevo es `ITableGame` + una linea (`class XGameModule() : TableGameModule(new XGame())`).
- **Mesas de jugador:** cualquiera crea una mesa (entrada, cantidad de jugadores, publica o privada con codigo de 6 caracteres), otros entran, el dueño inicia. Una persona esta
  sentada en **una sola mesa activa por juego**. Las mesas privadas no aparecen en el listado ni se revelan a quien no esta sentado.
- **Bots:** el dueño puede sumar bots (para jugar solo o completar la mesa). Cada juego aporta `BotAction`. Un bot **no reserva fichas**: la casa respalda lo que pone (el pago
  de la Wallet ya soporta cobrar mas de lo apostado, como la ruleta). Un jugador que deja vencer 3 turnos seguidos queda **ausente** y un bot juega por el (sigue siendo suyo
  el resultado): nadie puede trabar la mesa y no se puede abandonar en plena partida para escapar de una perdida.
- **Fichas en la Wallet, sin tocarla (casi):** cada asiento humano es una apuesta del protocolo de rondas (`TableBet`): reserva al sentarse, queda activa hasta terminar y
  se liquida con lo que se llevo el asiento (`RoundResolved`). Los pagos tienen que **sumar exactamente lo puesto entre todos** (bots incluidos); si el juego devuelve una cuenta
  que no cierra, se devuelve la entrada de cada uno y se registra el error: nadie pierde por un bug. Salir antes de empezar, cancelar una mesa vieja o una reserva que llega
  tarde devuelven lo apostado. **Cambio en la Wallet:** el tope del plazo de una reserva (`TtlSeconds`) sube de 900 a **3.600 s** (una mesa espera jugadores y juega); sigue
  siendo una red de seguridad para que una mesa colgada no retenga fichas para siempre.
- **Una partida = documentos con concurrencia optimista** (`[Version]`), igual que Crash y Blackjack: la jugada del jugador y la del motor (un bot, un turno vencido) compiten por
  la misma mesa, gana la primera y la segunda vuelve a leer y reintenta con el estado nuevo. Como el estado vive en la base, **una partida sobrevive a un reinicio**.
- **Motor unico:** un servicio en segundo plano (`Tables:EngineEnabled`, una sola instancia) mira cada pocos cientos de ms que mesas tienen algo para hacer solas (`NextAutoAt`):
  juega los bots, juega el turno de quien no actuo y cancela las mesas abiertas que nadie inicio (15 minutos).
- **Informacion oculta:** cada jugador recibe solo la **vista de su asiento** (`ITableGame.View`); el estado completo no sale del servidor. El aviso en vivo (`GameBroadcast`) solo
  dice "la mesa X cambio" (sin cartas ni datos) y el navegador vuelve a pedir lo suyo. Los asientos se llaman "Jugador N" o "Bot N": **nadie ve la identidad de otro**.
- **Provably fair:** la semilla se genera al crear la mesa (antes de que nadie se siente), su compromiso (SHA-256) es publico desde ese momento, el mazo sale de ella
  (`FairShuffle`: Fisher-Yates con HMAC-SHA256 y rechazo, igual que el zapato de Blackjack pero con una etiqueta por juego) y se revela al terminar. **Limite honesto:** el estado de la
  partida (que incluye el mazo restante) se guarda en la base en claro mientras se juega, asi que la semilla cifrada protege del jugador pero no de quien lea la base; en Blackjack
  la carta tapada si se calcula recien al darse vuelta. Para partidas entre jugadores sin casa de por medio, el siguiente paso seria el aporte de semilla de cada participante.

## Consecuencias
- Agregar Truco o Poker es escribir sus reglas (y su pagina del front): la plataforma ya tiene mesas, bots, tiempos, fichas, tests de integracion y endpoints.
- Un solo motor de mesas (como Crash y Blackjack): no hay reparto de mesas entre instancias todavia.
- Quedan para despues: reconexion con sala de espera persistente, chat, expulsar a un jugador, torneos con varias mesas y el aporte de semilla de cada jugador.
