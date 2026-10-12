# Poker (Texas Hold'em)

**Texas Hold'em por manos**, de 2 a 6 jugadores (solo personas: **no hay bots**). Corre sobre la [plataforma de mesas entre jugadores](juegos-mesas.md)
([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)). Cada uno entra con la misma cantidad de fichas y se juegan manos una tras otra: las fichas pasan de unos a otros y quien se queda
sin fichas queda eliminado. La partida termina cuando **queda un solo jugador con fichas**, que cobra todo lo que se puso entre todos.

> Son las **fases 1 y 2** del diseño de [ADR 0015](adr/0015-poker-continuo-torneo-y-mesa-libre.md): la base común (varias manos, pozos laterales, semilla por mano, sin bots) y el **torneo** (ciegas
> elegidas y crecientes, recompras, premio repartido entre los primeros y comisión de inscripción de la casa). Falta la fase 3, la mesa libre con rake.

## Cómo es una mano
1. **Ciegas:** las elige quien crea la mesa (ciega grande par; la chica es la mitad; por defecto `2 x max(1, entrada / 50)`) y se ponen solas. Suben con el torneo (ver abajo). El boton (D) se sortea con la semilla en la primera mano y despues **rota** al siguiente jugador con fichas.
   Con 2 jugadores (los que siguen con fichas) el que tiene el boton pone la ciega chica y juega primero; con más, la chica es la siguiente al boton, la grande la que sigue y juega primero la siguiente a la grande.
2. **Preflop:** cada uno recibe 2 cartas tapadas y se apuesta. Quien no tiene fichas no recibe cartas ni pone ciegas.
3. **Flop** (3 cartas comunitarias), **turn** (1) y **river** (1), cada una con su ronda de apuestas (juega primero el primero a la izquierda del boton que siga en la mano).
4. **Showdown:** quienes siguen muestran sus cartas; gana la mejor mano de 5 cartas entre las 2 propias y las 5 comunitarias. Si todos menos uno se retiran, ese se lleva el pozo **sin mostrar**.
5. **Pausa:** al terminar la mano hay unos **6 segundos** y arranca la siguiente (la plataforma juega ese "turno automatico": no cuenta como falta de nadie).

### Apuestas
Límite libre (*no limit*): en tu turno podés **retirarte**, **pasar** (si no hay nada que pagar), **igualar** o **subir** hasta todo lo que tengas (all-in). Subir es "a cuánto": como mínimo la
subida anterior (la primera apuesta de una ronda, como mínimo la ciega grande). Un all-in menor al mínimo es válido. Una subida obliga a todos a responder de nuevo. **No se puede subir si todos los
demás que siguen en la mano ya están all-in** (nadie podría responder): solo igualar o retirarse. Si todos los que siguen están all-in, se reparten las cartas que faltan hasta el showdown.

### Pozos laterales
Como las pilas cambian de una mano a otra, un all-in por menos que los demás arma **pozos laterales**:
- Hay un pozo por cada nivel de aporte de quienes siguen en la mano. Cada pozo suma lo que cada jugador (también los retirados) puso entre un nivel y el anterior, y lo gana **la mejor mano entre quienes
  llegaron a ese nivel**. Lo que un retirado puso por encima del mayor nivel va al último pozo.
- Una apuesta que nadie igualó queda como un pozo de un solo jugador: **vuelve a su dueño**.
- Si hay empate en un pozo se divide en partes iguales y el resto (si no se divide justo) va al primero de los ganadores a la izquierda del boton.
- Lo que cobra cada asiento se suma a su pila al terminar la mano; la suma de todas las pilas es siempre `entrada x asientos`.

## Torneo
La mesa es un **torneo**: todos entran con la misma pila y la partida dura hasta que uno se queda con todas las fichas.

- **Ciegas crecientes por cantidad de manos:** cada **10 manos** la ciega grande sube un **50 %** (redondeada hacia arriba y a par; la chica es la mitad): con 4, `4 → 6 → 10 → 16 → 24...`. Así el torneo siempre termina y no depende
  del reloj ni de las ausencias. El nivel que viene se ve en la pantalla.
- **Entrada:** tiene que valer entre **20 y 100 ciegas grandes** (entrada mínima 40, con ciega 2). Quien crea la mesa elige la ciega grande y las recompras; la casa fija el resto (comisión, niveles, pausas).
- **Recompras (0, 1 o 2 por jugador, las elige quien crea la mesa):** quien se queda sin fichas y todavía tiene recompras tiene una ventana de **20 segundos** (la pausa entre manos) para elegir **Recomprar** o **No, gracias**
  (aunque no sea su turno). Recomprar da una pila nueva igual a la inicial; si no decide a tiempo, o no le quedan, queda **eliminado**. Cuando todos los que podían recomprar decidieron, la pausa se acorta a 3 segundos.
  Si queda uno solo con fichas y nadie más puede recomprar, el torneo termina ahí.
- **Ranking:** gana quien se queda con todas las fichas; después vienen los eliminados, del último al primero en irse. Si dos se eliminan en la misma mano, queda mejor el que arrancó la mano con más fichas.
- **Premio repartido entre los primeros:** el premio es **una entrada por cada asiento y por cada recompra** (sin la comisión) y se reparte según cuántos juegan: con **2**, todo para el primero; con **3 o 4**, 65 % y 35 %; con **5 o 6**,
  50 %, 30 % y 20 %. El resto del redondeo va al primero.

### Lo que se reserva y la comisión de la casa
- **Comisión de inscripción:** el **10 %** de la entrada (configurable, la fija la casa), por cada entrada: la inicial y cada recompra. No entra al premio: se la queda la casa.
- **Se reserva todo al sentarse:** `(entrada + comisión) x (1 + recompras de la mesa)`. Es una decisión de implementación (ADR 0015): evita reservas nuevas en la Wallet a mitad de partida, que podrían fallar o vencer, y deja el torneo
  determinista. Lo que no se usa **se devuelve al terminar**: cobrás `premio de tu lugar + (recompras sin usar) x (entrada + comisión)`.
- Al cerrar, la plataforma verifica `pagos + lo que se queda la casa = lo reservado`; si no cierra, devuelve a cada uno lo que reservó. Antes de crear la mesa, `POST /games/poker/quote` dice cuánto se reserva y por qué (o el motivo si no es válida).

### Configuración (sección `Poker` del appsettings, la fija la casa)
`EntryFeePermille` (100 = 10 %), `BlindLevelHands` (10), `BlindGrowthPercent` (50), `MinEntryInBigBlinds` (20), `MaxEntryInBigBlinds` (100), `MaxRebuys` (2), `BetweenHandsSeconds` (6), `RebuyWindowSeconds` (20) y
`AfterDecisionsSeconds` (3). Lo elegido al crear una mesa (ciega, recompras, comisión, niveles) queda guardado en ella: cambiar la configuración no altera mesas ya creadas.

### Manos (de mayor a menor)
Escalera de color (la de As arriba es la "escalera real") · Póker · Full · Color · Escalera (el As también vale 1: A-2-3-4-5 es la más baja) · Trío · Doble pareja · Pareja · Carta alta. Se desempata por las cartas de
mayor rango; los palos no desempatan.

## Tiempos y ausencias
**30 segundos por turno.** Si no actuás a tiempo se pasa (si es gratis) o te retirás; con 3 turnos seguidos sin actuar quedás **ausente**: la mesa hace por vos lo mismo que cuando se vence tu turno (pasar
o retirarse, **nunca apostar**) hasta que vuelvas a jugar. Sus ciegas siguen saliendo de su pila, así que un ausente termina eliminado. No hay bots y no se puede salir en plena partida.

## Provably fair
Cada **mano** tiene su propia semilla: `semilla_mano = HMAC-SHA256(clave = semilla de la mesa, "poker:{idDeLaMesa}:hand:{n}")` en hex. Su **compromiso** (`SHA-256(semilla_mano)`) está en la vista antes de repartir
y la semilla se revela cuando la mano termina (los hashes de manos distintas no se pueden relacionar entre sí). El mazo se baraja con `FairShuffle` (Fisher-Yates con
`HMAC-SHA256(semilla_mano, "poker:{idDeLaMesa}:h{n}:{contador}")`); con `m` jugadores con fichas, la primera carta del k-ésimo es la posición `k`, la segunda `m + k`, y después siguen el flop (`2m` a `2m+2`), el turn
(`2m+3`) y el river (`2m+4`), sin quemar cartas. El asiento del boton de la primera mano sale de la etiqueta `"poker:{id}:h1:dealer"` con la semilla de esa mano. La semilla de la mesa se revela al terminar la
partida y con ella se recalculan todas las manos. Cada jugador ve solo **sus cartas**; las de los demás no salen del servidor hasta el showdown.

## Cartas
Un entero de 0 a 51: `palo = id / 13` (0 picas, 1 corazones, 2 diamantes, 3 tréboles) y `rango = (id % 13) + 2` (2 a 10, J = 11, Q = 12, K = 13, A = 14).

## Jugadas (`POST /games/poker/tables/{id}/action`)
`{"type":"fold"}` · `{"type":"check"}` · `{"type":"call"}` · `{"type":"raise","to":40}` (a cuánto subís en total en esta ronda; all-in = tu máximo). Entre manos existen `{"type":"next"}`, que juega la
plataforma cuando vence la pausa (el asiento del boton también puede mandarlo para no esperar), y `{"type":"rebuy"}` / `{"type":"decline"}` para quien se quedó sin fichas (aunque no sea su turno).
Crear la mesa: `POST /games/poker/tables` con `{ buyIn, maxPlayers, isPrivate, name, options: { bigBlind, rebuys } }`.
La vista de tu asiento (`game`) trae tus cartas, las comunitarias, el pozo, cada jugador (pila, apuesta, retirado, all-in, eliminado), cuánto te falta para igualar (`toCall`), el rango de subida (`minRaiseTo`..`maxRaiseTo`),
las jugadas legales (`actions`), el showdown, los pozos de la mano (`pots`), el número de mano (`handNo`), `done` (la **mano** terminó) y `over` (la **partida** terminó), el compromiso de la mano (`commitment`), su semilla
cuando se revela (`handSeed`), las ciegas y el nivel que viene (`smallBlind`, `bigBlind`, `nextBigBlind`, `handsToNextLevel`), las recompras (`rebuys`, y por jugador `rebuysLeft` y `awaitingRebuy`),
el premio (`prizePool`, `prizes` por lugar) y el `ranking` final, y los últimos eventos.
