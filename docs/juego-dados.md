# Dados (over/under)

Uno de los juegos mas usados de los casinos con fichas y el mas simple de portar: una tirada, una decision, sin estado entre rondas. Es un modulo
`src/Modules/Games.Dice` que sigue el contrato de [juegos-contrato.md](juegos-contrato.md) (mismo protocolo de rondas con la Wallet que la ruleta).

## Reglas
- El servidor sortea un numero de **0,00 a 99,99** (10.000 resultados, en centesimas: `0..9999`) con el RNG provably fair ([provably-fair.md](provably-fair.md)); el nonce lo asigna el servidor.
- El jugador elige la **probabilidad de ganar** (`chance`, en centesimas de punto porcentual: `4950` = 49,50 %; de 2 % a 95 %) y la **direccion**:
  - `Under`: gana si el numero es menor que `chance`.
  - `Over`: gana si el numero es mayor o igual que `10000 - chance` (el tramo superior del mismo ancho).
- Paga `apuesta x 9800 / chance`, redondeado hacia abajo (siempre entero): ventaja de la casa del **2 %** para cualquier probabilidad. Paga la apuesta incluida.
- Apuesta de 1 a 10.000 fichas. El premio maximo es x49 (probabilidad del 2 %).

## API
- `POST /games/dice/bets` con `{ direction: "Under"|"Over", chance, stake }` y `Idempotency-Key` → `202` con el `BetId`, el compromiso y el nonce.
- `GET /games/dice/rounds/{betId}` · `GET /games/dice/rounds?limit=` (solo rondas propias; una ajena responde 404).
- El resultado viaja en el aviso en vivo `roundClosed` (`winningNumber` = el numero en centesimas, `game` = `Dice`).

## Front
`web/src/pages/Dice.tsx` (ruta `/dados`): selector de direccion, deslizador de probabilidad, `StakePicker` compartido y el numero que "rueda" hasta que llega el resultado
(respeta `prefers-reduced-motion`). Las cuentas de la logica (`payoutFor`, `wins`) viven en `web/src/lib/dice.ts` y replican las del servidor.

## Pruebas
`tests/Games.Tests/DiceTests.cs` (reglas, ventaja del 2 %, transiciones idempotentes), `tests/Integration.Tests/Games/DiceFlowTests.cs` (Postgres + RabbitMQ reales: saldo cerrado,
verificacion del sorteo, idempotencia, saldo insuficiente, apuestas invalidas) y `web/src/pages/Dice.test.tsx`.

## Pendiente
- El historial (`/historial`) y el lobby ("ultimas jugadas") todavia solo muestran rondas de ruleta.
- Un limite de premio/apuesta configurable desde el backoffice (hoy son constantes en `DiceBet`).
