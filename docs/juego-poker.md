# Poker (Texas Hold'em)

**Texas Hold'em de una mano por mesa**, de 2 a 6 jugadores (personas o bots). Corre sobre la [plataforma de mesas entre jugadores](juegos-mesas.md)
([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)). Cada uno entra con la misma cantidad de fichas, se juega **una mano** completa y al terminar cada asiento cobra **lo que le
queda de su entrada** (lo que no apostó, más lo que ganó del pozo). Es un "sit & go" de una mano: no hay rebuy ni mano siguiente.

## Cómo es una mano
1. **Ciegas:** la ciega chica es `max(1, entrada / 50)` y la grande es el doble; se ponen solas. El boton (D) se sortea con la semilla. Con 2 jugadores el que tiene el boton pone la ciega
   chica y juega primero; con más, la chica es la siguiente al boton, la grande la que sigue y juega primero la siguiente a la grande.
2. **Preflop:** cada uno recibe 2 cartas tapadas y se apuesta.
3. **Flop** (3 cartas comunitarias), **turn** (1) y **river** (1), cada una con su ronda de apuestas (juega primero el primero a la izquierda del boton que siga en la mano).
4. **Showdown:** quienes siguen muestran sus cartas; gana la mejor mano de 5 cartas entre las 2 propias y las 5 comunitarias. Si todos menos uno se retiran, ese se lleva el pozo **sin mostrar**.

### Apuestas
Límite libre (*no limit*): en tu turno podés **retirarte**, **pasar** (si no hay nada que pagar), **igualar** o **subir** hasta todo lo que tengas (all-in). Subir es "a cuánto": como mínimo la
subida anterior (la primera apuesta de una ronda, como mínimo la ciega grande). Un all-in menor al mínimo es válido. Una subida obliga a todos a responder de nuevo. Si todos los que siguen están
all-in, se reparten las cartas que faltan hasta el showdown.

### Un solo pozo
Como todos empiezan con lo mismo, **no hay pozos laterales**: todos los que llegan al showdown pusieron lo mismo. Si hay empate se divide el pozo en partes iguales y el resto (si no se divide justo)
va al primero de los ganadores a la izquierda del boton. Lo que se lleva cada asiento al terminar es `entrada − lo que puso + lo que ganó` y la suma de todos es exactamente lo que se puso.

### Manos (de mayor a menor)
Escalera de color (la de As arriba es la "escalera real") · Póker · Full · Color · Escalera (el As también vale 1: A-2-3-4-5 es la más baja) · Trío · Doble pareja · Pareja · Carta alta. Se desempata por las cartas de
mayor rango; los palos no desempatan.

## Tiempos y ausencias
**30 segundos por turno.** Si no actuás a tiempo se pasa (si es gratis) o te retirás; con 3 turnos seguidos sin actuar quedás **ausente** y juega un bot por vos.

## Provably fair
Al crear la mesa se publica el **compromiso** (SHA-256 de la semilla); al terminar se revela. El mazo se baraja con `FairShuffle` (Fisher-Yates con `HMAC-SHA256(semilla, "poker:{idDeLaMesa}:{n}")`):
la primera carta de cada asiento es la posición `s` (en orden de asiento, de 0), la segunda la `N + s`, y después siguen el flop (`2N` a `2N+2`), el turn (`2N+3`) y el river (`2N+4`), sin quemar cartas.
El asiento del boton sale de `"poker:{id}:dealer"`. Cada jugador ve solo **sus cartas**; las de los demás no salen del servidor hasta el showdown.

## Cartas
Un entero de 0 a 51: `palo = id / 13` (0 picas, 1 corazones, 2 diamantes, 3 tréboles) y `rango = (id % 13) + 2` (2 a 10, J = 11, Q = 12, K = 13, A = 14).

## Jugadas (`POST /games/poker/tables/{id}/action`)
`{"type":"fold"}` · `{"type":"check"}` · `{"type":"call"}` · `{"type":"raise","to":40}` (a cuánto subís en total en esta ronda; all-in = tu máximo).
La vista de tu asiento (`game`) trae tus cartas, las comunitarias, el pozo, cada jugador (pila, apuesta, retirado, all-in), cuánto te falta para igualar (`toCall`), el rango de subida (`minRaiseTo`..`maxRaiseTo`),
las jugadas legales (`actions`), el showdown y los últimos eventos.
