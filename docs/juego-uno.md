# Uno

Un juego de cartas **entre 2 y 6 jugadores** (personas o bots): quedate sin cartas antes que los demás y te llevás **todo lo que se puso en la mesa**. Es el primer juego sobre la
[plataforma de mesas entre jugadores](juegos-mesas.md) ([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)).

## Reglas
- Mazo de **108 cartas**: por cada color (rojo, amarillo, verde, azul) un 0, dos de cada 1 a 9, dos saltos, dos reversas y dos +2; más 4 comodines y 4 comodines +4.
- Se reparten **7 cartas** a cada uno y se da vuelta una (nunca un comodín: si sale, va al fondo del mazo). Empieza un asiento sorteado con la misma semilla; si la primera carta
  es de acción se aplica (salto, reversa, +2).
- En tu turno jugás una carta del **mismo color**, del **mismo número o tipo** que la de arriba, o un **comodín** (elegís el color que manda).
- **Salto:** el siguiente pierde el turno. **Reversa:** cambia el sentido (con 2 jugadores es otro salto). **+2:** el siguiente roba 2 y pierde el turno. **Comodín +4:** el
  siguiente roba 4 y pierde el turno, y **solo se puede jugar sin cartas del color que manda**. No se apilan los +2/+4 y el "¡Uno!" es automático.
- Sin carta para jugar **robás una**: si se puede jugar, la jugás o pasás; si no, el turno pasa solo. Si se acaba el mazo se rebaraja el descarte (menos la carta de arriba).
- **Tenés 30 segundos por turno.** Si no jugás, robás/pasás por vos; con 3 turnos seguidos sin jugar quedás **ausente** y juega un bot por vos (la partida no se traba).
- Gana quien se queda sin cartas y cobra `entrada x cantidad de asientos` (bots incluidos: la casa respalda lo que pone un bot).

## Entrar a jugar
Elegí una mesa abierta o creá la tuya (entrada de 10 a 1.000 fichas, 2 a 6 jugadores, pública o privada con código). Sumá **bots** si querés jugar solo o completar la mesa y
empezá cuando estés listo. Las fichas se reservan al sentarte (y se devuelven si te vas antes de empezar o si nadie inicia la mesa en 15 minutos).

## Provably fair
Al crear la mesa se publica el **compromiso** (SHA-256 de la semilla); al terminar se revela. El mazo sale de la semilla: `FairShuffle` mezcla `0..107` con Fisher-Yates y
`HMAC-SHA256(semilla, "uno:{idDeLaMesa sin guiones}:{n}")` (8 palabras de 32 bits por bloque, con rechazo para no sesgar). El asiento que empieza sale de
`"uno:{id}:first"`, y cada rebarajado del descarte usa `"uno:{id}:r{n}"`. Vectores de una implementación independiente (Python): semilla `tables-test-seed`, etiqueta
`uno:00000000000000000000000000000001` → primeras 10 cartas `69, 52, 95, 93, 32, 25, 41, 83, 8, 90`.
Cada jugador ve solo **su mano** y cuántas cartas tiene cada uno; el mazo y las manos ajenas nunca salen del servidor.

## Cartas
Un entero de 0 a 107: `0..99` son de color (`color = id / 25`: 0 rojo, 1 amarillo, 2 verde, 3 azul; `id % 25`: 0 = cero, 1 a 9 = 1 a 9, 10 a 18 = 1 a 9 otra vez, 19-20 = salto,
21-22 = reversa, 23-24 = +2); `100..103` comodines y `104..107` comodines +4.

## Jugadas (`POST /games/uno/tables/{id}/action`)
`{"type":"play","card":17}` · `{"type":"play","card":101,"color":2}` (comodín: elegís el color) · `{"type":"draw"}` · `{"type":"pass"}` (solo después de robar una carta jugable).
La vista de tu asiento (`game`) trae tu mano, qué podés jugar ahora, cuántas cartas tiene cada uno, la carta de arriba y su color, el sentido y los últimos eventos.
