# ADR 0013 - Tragamonedas: cascadas con multiplicador

- **Estado:** aceptada
- **Fecha:** 2026-10-10

## Contexto
La tragamonedas (ADR 0006) era de un solo golpe: se sortean tres rodillos, se paga o no y se termina. Para que el juego tenga mas emocion queremos **cascadas**: cuando un premio
supera la apuesta, los rodillos que lo forman explotan, se sortean de nuevo y el siguiente premio vale mas. La condicion de siempre sigue en pie: el pago es un **entero**, el giro es
**determinista y verificable** (provably fair) y el **retorno al jugador (RTP) nunca supera el 100%**.

## Decision
- **Dentro de la tragamonedas actual** (mismo juego, mismos endpoints, misma Wallet y mismo documento de giro): no hay un juego nuevo.
- **Regla de la cadena.** El giro inicial vale x1. Si su premio paga **al menos x2** (`CascadeMinPay`: supera la apuesta; un "recupero" x1 no encadena), los rodillos que forman el
  premio (la racha desde la izquierda) se sortean de nuevo y el premio de la nueva combinacion se multiplica por el siguiente valor de `CascadeMultipliers` (**x1, x2, x3, x5, x10**). La cadena
  sigue mientras haya premios que encadenan y **se corta sola** al agotarse los multiplicadores: son como mucho 5 pasos, asi que **el pago maximo tiene tope** (premio mayor de la tabla x 21).
  Los multiplicadores estan **fijos en el codigo**, no en la configuracion: cambiarlos altera el retorno de todas las tablas y es un cambio de reglas.
- **RTP exacto, no simulado.** Se penso validar el retorno por simulacion con un tope de pago, pero con tres rodillos el espacio es chico y se puede calcular **exacto con enteros**:
  `SlotsPaytable` recorre la cadena de atras para adelante (programacion dinamica sobre `BigInteger`, con todo expresado sobre `TotalWeight^(rodillos x pasos)`) y obtiene la esperanza
  del multiplicador sin una sola aproximacion. Sigue valiendo la regla "una tabla con retorno mayor al 100% se rechaza" (al arrancar y en el panel de administracion). Un test compara
  el calculo con una **segunda implementacion independiente** (recursion con decimales) y con una **simulacion de 200.000 giros**; otro mide que validar la tabla mas grande permitida
  (12 simbolos) tarda una fraccion de segundo, porque el panel la valida en cada edicion.
- **Determinismo.** Los tres rodillos iniciales consumen tres numeros del flujo justo (`FairRng`), igual que antes; en cada cascada, los rodillos que vuelven a girar consumen uno cada uno,
  de izquierda a derecha. Mismas seeds y nonce, mismo giro completo: el jugador lo recalcula con `SlotsGame.Play`.
- **Que se guarda y que se muestra.** El giro guarda `Steps` (cada paso: simbolos, premio de la tabla y multiplicador); `Reels` sigue siendo el giro inicial y `Multiplier` el **total**
  (suma de `premio x multiplicador` de cada paso). `Payout = apuesta x Multiplier`. La tabla publica agrega `cascadeMultipliers` y `cascadeMinPay`. El front reproduce los pasos (explotan los
  rodillos del premio, caen simbolos nuevos con el multiplicador mas alto) y **recien al terminar** muestra el resultado y libera el saldo, igual que con los rodillos.
- **Tabla por defecto reequilibrada.** Las cascadas suman retorno (la tabla anterior pasaria de 96,14% a ~125%), asi que se bajaron los premios: cereza x4, limon x6, naranja x8, campana x14,
  bar x30, siete x60 (las rachas de cereza no cambian). Retorno **96,6%** y frecuencia de premio 33,7% (la del giro inicial, igual que antes).

## Consecuencias
- El retorno de una tabla ahora incluye las cascadas: el panel de administracion muestra el retorno total y una tabla "generosa" que antes era valida puede ser rechazada.
- Los giros anteriores a esta decision no tienen `Steps` (el front los muestra con `Reels`) y **no se pueden recalcular con las reglas nuevas**: se guardaron con el historial tal cual.
- Cambiar `CascadeMultipliers` o `CascadeMinPay` invalida la verificacion de los giros hechos con las reglas anteriores: si algun dia se cambian, hay que versionar las reglas como se versiona la tabla.
- Queda para despues: cascadas sobre una grilla (varios simbolos por rodillo), simbolos comodin y tiradas gratis.
