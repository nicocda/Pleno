# ADR 0010 - Juegos de mesa: un modelo de mesa comun

- **Estado:** propuesta (para discutir antes de implementar)
- **Fecha:** 2026-10-09

## Contexto
Ruleta y tragamonedas son de una tirada por jugador; Crash es una ronda compartida por todos (ADR 0009). Los juegos de cartas que se juegan en **mesas**
(Blackjack, Poker, Uno, Truco) son otra cosa: muchas partidas chicas y simultaneas (2 a 6 jugadores cada una), con turnos, decisiones del jugador durante la
partida, **informacion oculta** (las cartas de cada uno) y, en varios casos, jugadores que apuestan **entre si** y no contra la casa.
Se pregunto si conviene que el casino ponga mesas fijas o que los jugadores las creen.

## Decision
- **Un solo modelo de `Mesa`**, con dos formas de nacer: **mesas fijas** (las crea el casino por configuracion: Blackjack, Poker publico) y **mesas de jugador** (las crea un
  jugador con sus reglas, publica o privada con codigo de invitacion: Truco, Uno). Una mesa tiene asientos, reglas del juego, apuesta de entrada, y un estado
  (esperando, jugando, terminada). Cada juego implementa sus reglas sobre ese modelo, como ya hace con el contrato de juegos (ADR 0008).
- **Una mesa = un stream de eventos** en Marten ("se repartio", "A pidio carta", "A se planto"): historial completo, partida reconstruible y auditable.
- **Un solo procesador por mesa:** las acciones de una mesa se procesan **de a una y en orden** (mensajes de la misma mesa al mismo manejador secuencial en Wolverine);
  mesas distintas corren en paralelo. Asi no hay carreras dentro de una mesa. Con una sola instancia del servicio de juegos no hace falta mas; con varias, las acciones de
  una mesa deben ir siempre a la misma instancia (afinidad), y eso se resolvera cuando haya mas de una (igual que el motor de Crash).
- **Fichas en la Wallet:** se reserva al sentarse (o al apostar la mano) y se liquida al terminar, con el mismo protocolo reservar -> resolver -> liquidar -> cerrar,
  extendido a **varios participantes**: las liquidaciones de una partida suman cero (menos la comision de la casa si existe). La Wallet no cambia.
- **Informacion oculta:** al cliente solo viaja lo que le corresponde. Un grupo de SignalR por mesa para lo publico y mensajes individuales para la mano de cada
  jugador. Es el punto de seguridad principal: nunca se envia una carta ajena antes de mostrarse.
- **Mazo provably fair:** compromiso (hash de la semilla) antes de repartir y semilla revelada al terminar; barajado determinista (Fisher-Yates con HMAC). En juegos entre
  jugadores se puede sumar una semilla de cada participante (commit-reveal) para que nadie dependa solo del casino.
- **Turnos con tiempo limite:** si un jugador no actua a tiempo, se planta o abandona y sigue el siguiente; una desconexion no bloquea la mesa (hay un margen para
  reconectar).

## Orden de implementacion
1. **Blackjack** con mesas fijas contra la casa: valida el modelo de mesa, las manos ocultas, los turnos y el mazo con la menor dificultad de reglas.
2. **Truco o Uno** entre jugadores: suma mesas creadas por jugadores, invitaciones y partidas con jugadores que se van.
3. **Poker**: suma pozos, rondas de apuestas y el mazo con aportes de varios participantes.

## Consecuencias
- La concurrencia de mesas es **mas simple** que la de Crash (no hay carrera retiro contra explosion): el orden dentro de la mesa lo da el procesador secuencial.
- Hay que disenar con cuidado el protocolo con la Wallet para varios participantes (reservas por asiento, liquidacion que suma cero) y cubrirlo con tests de integracion.
- Queda para mas adelante: balancear mesas entre varias instancias, reconexion con partida en curso tras un reinicio del servicio, y limpieza de mesas abandonadas.
