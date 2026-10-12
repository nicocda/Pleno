# ADR 0015 - Poker continuo: torneo y mesa libre

- **Estado:** propuesta (se acepta al cerrar la primera fase de implementacion)
- **Fecha:** 2026-10-12

## Contexto
El Poker actual (ADR 0012, [juego-poker.md](../juego-poker.md)) es un *sit & go de una mano*: todos entran con lo mismo, se juega **una** mano y la mesa cierra. Eso tiene tres limites:
no hay continuidad (una mesa no es un ciclo de manos), todos empiezan con las mismas fichas (por eso hoy hay **un solo pozo**, sin pozos laterales) y la casa no gana nada (los bots
los respalda ella y no hay comision). Queremos una mesa donde se juegue de forma continua con las fichas propias, con las ciegas definidas por la mesa, y que la casa gane.

## Decision
El poker de una mano **deja de existir**. Quedan dos modalidades sobre la misma base:

### Base comun (las dos)
- **Varias manos por mesa.** Una mano termina, se pagan los pozos dentro de la mesa y empieza la siguiente (el boton rota; con menos de 2 jugadores con fichas la mesa espera o termina).
- **Pilas distintas = pozos laterales.** Como las pilas cambian de mano en mano, un all-in por menos que los demas genera **pozo principal y pozos laterales**; cada pozo lo gana el mejor
  entre quienes aportaron a el. Es la parte mas delicada: se prueba con tests de propiedades (las fichas se conservan en cada mano, con empates, retiros y varios all-in).
- **Ciegas elegidas al crear la mesa** (ciega grande par; la chica es la mitad). Ya no se derivan de la entrada.
- **Sin bots.** La casa no pone fichas en juego. Quien esta **ausente** (3 turnos vencidos) no apuesta: pasa si es gratis y se retira si no. Los tests siguen usando una estrategia
  de prueba interna (no expuesta) para jugar partidas completas. Para probar a mano hacen falta dos cuentas.
- **Semilla por mano.** La semilla unica por mesa no sirve con varias manos (revelarla al final deja sin verificar cada mano y revelarla antes expone las siguientes). Cada mano
  deriva su semilla de un secreto de la mesa (`HMAC(secreto, "poker:{mesa}:hand:{n}")`), **publica su compromiso antes de repartir** y la revela al terminar la mano, como Blackjack.
- **Rake / comision de la casa** (ver mas abajo). Nuevo invariante contable: `pagos + comision de la casa = lo reservado`.

### Torneo
- **Entrada fija** (la elige quien crea la mesa) y misma pila inicial para todos. Con comision de inscripcion encima (no entra al premio).
- **Ciegas crecientes por cantidad de manos, no por tiempo:** cada **10 manos** sube el nivel (propuesta: la ciega grande sube un 50 %, redondeada hacia arriba y a par). Asi es
  determinista, se prueba facil y una ausencia no lo altera.
- **Recompras limitadas: 0, 1 o 2** (se elige al crear la mesa). Solo al quedarte en 0 fichas y dentro de una ventana corta (propuesta: 20 s); cuesta una entrada **mas su comision** y da
  la pila inicial. Reserva nueva en la Wallet entre manos; si falla o no se responde, quedas eliminado.
- **Termina** cuando, cerradas las ventanas de recompra, queda **un solo jugador con fichas**.
- **Premio repartido entre los primeros** (propuesta a confirmar, por la cantidad de jugadores): 2 jugadores, todo al primero; 3 o 4, 65 % / 35 %; 5 o 6, 50 % / 30 % / 20 %. El resto
  del redondeo va al primero. Quien se va a mitad pierde su entrada; sus ciegas se pagan solas hasta que se queda sin fichas.
- Encaja con la plataforma actual: termina con un solo `Outcome` por asiento.

### Mesa libre (cash)
- Se entra con las fichas que se quiera dentro de un **rango por mesa: de 20 a 100 ciegas grandes**. Se puede **recargar entre manos** hasta el maximo (nunca durante una) y, si te
  quedas en 0, tienes una ventana para volver a entrar.
- Te puedes **ir entre manos cobrando tu pila**. La mesa no termina: espera si queda menos de 2 jugadores.
- **Cambios de plataforma:** liquidar **un asiento a mitad de mesa** (hoy solo hay `Outcome` al final) y que la reserva de la Wallet no venza mientras haya pila (hoy `TtlSeconds` es 3.600 s).
  Las fichas pasan por una cuenta intermedia de la mesa para que cada salida cuadre y el rake quede reconocido en el momento.

### Como gana la casa
- **Mesa libre: rake por pozo, siempre en porcentaje, sin tope por mano.** Lo fija **siempre la casa** (configuracion, no el creador de la mesa); propuesta inicial **3 %** (se guarda en
  milesimas: 30 ‰). Se calcula sobre cada pozo (principal y laterales por separado), en fichas enteras y **redondeando hacia abajo**: un pozo chico puede pagar 0. Regla *no flop, no drop*:
  si la mano termina antes del flop no se cobra nada.
- **Torneo: comision de inscripcion** sobre la entrada (propuesta: 10 %) y tambien en cada recompra.
- Solo eso: sin cuota por mano ni bote especial. Se muestran en las reglas de la mesa y cada mano registra lo cobrado; el rake no afecta a las cartas. Se acredita a la casa en la Wallet
  (asientos de partida doble) y se ve en el backoffice.

## Consecuencias
- Mas piezas que el resto de juegos de mesa: pozos laterales, bucle de manos, semilla por mano, rake y (para la mesa libre) liquidacion por asiento. Por eso se hace **por fases**, cada una
  con su PR y sus tests (Wallet: unitarios + integracion con Testcontainers, segun las reglas del repo):
  1. Base comun: pozos laterales, varias manos, semilla por mano, sin bots.
  2. Torneo: ciegas crecientes, recompras, eliminacion, premios y comision.
  3. Mesa libre: rake, recarga, irse entre manos, liquidacion por asiento y reserva que no vence.
- El front de Poker cambia (cuenta atras entre manos, pila de cada uno, recomprar, irse); el historial de las manos de una mano sigue legible.
- **Riesgo conocido:** en mesa libre una persona con varias cuentas puede pasar fichas a otra perdiendo a proposito. Con fichas ficticias es inocuo; el rake lo encarece un poco. Si algun dia
  hay premios o promociones, hara falta deteccion (no entra en esta decision).
- Fuera de alcance: torneos con varias mesas, chat, expulsar jugadores, aporte de semilla de cada jugador (ver ADR 0012).

## Puntos a confirmar al implementar
Reparto exacto del premio, subida de la ciega por nivel, duracion de la ventana de recompra, % del rake y de la comision (todos son configuracion, no codigo).
