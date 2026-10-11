# Truco

Truco argentino **a dos jugadores** (con otra persona o contra un bot), con baraja española de 40 cartas y **a 15 puntos**. Quien llega primero se lleva **todo lo que se puso en la mesa**.
Corre sobre la [plataforma de mesas entre jugadores](juegos-mesas.md) ([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)). No tiene flor (por ahora).

## Cómo se juega una ronda
Se reparten **3 cartas** a cada uno y se juegan hasta **3 manos**: en cada una cada uno tira una carta y gana la mano la más fuerte; el que gana juega primero la siguiente. Gana la ronda quien
gana **2 manos**. Si hay **parda** (empate) en la primera mano decide la segunda; si la parda es la segunda, gana quien ganó la primera; si todas son pardas, gana quien **es mano** (el que juega
primero la ronda; "es mano" se alterna en cada ronda). En la mesa, las cartas se tiran **una mano encima de la otra** (la segunda mano sobre la primera), como en una mesa de verdad. Una ronda vale **1 punto**, más lo que se haya cantado.

### Jerarquía de cartas (de más fuerte a más débil)
1 de espadas · 1 de bastos · 7 de espadas · 7 de oros · los 3 · los 2 · 1 de copas y de oros · los 12 · los 11 · los 10 · 7 de copas y de bastos · los 6 · los 5 · los 4.

## Cantos
Se cantan en tu turno (antes de tirar la carta); el otro responde **quiero**, **no quiero** o **sube**. Subir es aceptar lo anterior y cantar encima.
- **Truco** (la ronda vale 2) → **Retruco** (3) → **Vale cuatro** (4). Solo puede subir quien *no* hizo el último canto aceptado. Con *no quiero*, quien cantó se lleva lo que valía antes
  (1, 2 o 3 puntos) y la ronda termina.
- **Envido**, solo en la **primera mano** y antes de aceptar un truco: **envido** (2, se puede repetir una vez), **real envido** (3) y **falta envido** (lo que le falta al que va ganando para llegar
  a 15). Si se acepta, se comparan los puntos de envido y gana el más alto (**empate: gana el mano**); se muestran los puntos de los dos. Con *no quiero*, quien cantó se lleva lo que se había
  aceptado antes del último canto (al menos 1).
- **Al mazo:** te rendís y el otro se lleva lo que vale la ronda en ese momento (1, o lo que se haya aceptado).

### Puntos de envido
Con dos o más cartas del mismo palo: **20 + las dos mejores** (el 10, 11 y 12 valen 0). Sin palos repetidos: la carta más alta. Máximo 33.

## Tiempos y ausencias
**45 segundos por turno.** Si no jugás a tiempo se tira tu carta más baja (o se responde *no quiero* a un canto); con 3 turnos seguidos sin jugar quedás **ausente** y juega un bot por vos.

## Provably fair
Al crear la mesa se publica el **compromiso** (SHA-256 de la semilla); al terminar se revela. Cada ronda se baraja con `FairShuffle` (Fisher-Yates con `HMAC-SHA256(semilla, "truco:{idDeLaMesa}:h{n}")`,
`n` = número de ronda desde 1): las cartas del asiento 0 son las posiciones 0, 2 y 4 del mazo barajado y las del asiento 1 las 1, 3 y 5. El mano de la primera ronda sale de `"truco:{id}:mano"`.
Cada jugador ve solo **sus cartas** (y sus puntos de envido); las del otro no salen del servidor hasta que se juegan.

## Cartas
Un entero de 0 a 39: `palo = id / 10` (0 espadas, 1 bastos, 2 oros, 3 copas) e `id % 10` es la posición del número en `1, 2, 3, 4, 5, 6, 7, 10, 11, 12`.

## Jugadas (`POST /games/truco/tables/{id}/action`)
`{"type":"play","card":3}` · `{"type":"truco"}` / `retruco` / `vale4` · `{"type":"envido"}` / `real_envido` / `falta_envido` · `{"type":"quiero"}` / `no_quiero` · `{"type":"mazo"}`.
La vista de tu asiento (`game`) trae tus cartas y puntos de envido, las cartas jugadas, quién ganó cada mano (el servidor las llama `bazas`), el marcador, qué canto espera respuesta, **las jugadas que podés hacer ahora** (`actions`) y los últimos eventos.

## Ritmo
El bot **piensa un momento** antes de cada jugada o respuesta (`Tables:BotThinkMilliseconds`, 1,5 s por defecto) y la pantalla dice "Bot 2 está pensando…"; al terminar una ronda las cartas de la ronda que acaba de terminar quedan a la vista unos segundos (con quién la ganó) antes de pasar a la siguiente.
