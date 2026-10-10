# ADR 0014 - Cajeros: jerarquia, cargas por transferencia, comision y cobros

- **Estado:** aceptada (fases A, B y C implementadas)
- **Fecha:** 2026-10-10

## Contexto
Hoy el unico rol que mueve fichas "desde afuera" es `backoffice`: puede acreditar a cualquier jugador (`CreditAsync`, siempre con `IdempotencyKey` y auditoria). Queremos simular un mundo
mas natural: **cajeros** que cargan fichas a los jugadores de su jurisdiccion, una **jerarquia** (a quien pertenece cada jugador), un **incentivo** para que la carga ocurra y un
**proceso de cobro** (el camino inverso). Siguen valiendo las reglas del proyecto: montos enteros (`long`), el saldo se **deriva** de asientos de partida doble, toda operacion que mueve
fichas lleva `IdempotencyKey`, la identidad sale del token y la PII no va a logs.

## Decision
- **Dos roles en Keycloak, `head_cashier` (jefe de cajeros) y `cashier` (cajero); la jerarquia vive en nuestra base, no en Keycloak.** Keycloak solo responde "quien es cajero o jefe". Quien depende de quien es un dato de la
  **Wallet** (`HierarchyNode`: nivel y un solo padre, un arbol y no un grafo), porque la unica operacion que la consulta es la carga y asi no cruza servicios: el jefe no tiene padre, el padre de un cajero es un jefe y el de
  un jugador es un cajero. **Jefe de cajeros → solo cajeros; cajero → solo jugadores**, siempre de su jurisdiccion directa; `backoffice` arma el arbol. El rol del token tiene que coincidir con el nivel del arbol. Cajeros
  y jefes tambien son jugadores (conservan `player`) y operan con su **propia cuenta de fichas**.
- **Las fichas de una carga salen del saldo del cajero.** Backoffice le acredita fichas al cajero (como hoy a cualquiera) y el cajero las reparte: cargar es una **transferencia** cajero → jugador, no
  una emision. Asi nadie crea fichas de la nada, la liquidez del cajero es limitada y todo cuadra por partida doble. Hace falta una operacion nueva de la Wallet, `Transfer(from, to, key, amount)`,
  atomica (un debito y un credito en la misma transaccion), idempotente y con auditoria (quien, a quien, cuanto, cuando; solo ids). **Implementacion (fase A):** cada cuenta es un stream de eventos, asi que la transferencia
  escribe **un evento `ChipsTransferred` en cada stream, con los mismos asientos y el mismo id de transaccion, en una sola transaccion** (cada cuenta solo proyecta su mitad). La clave de idempotencia del que envia
  es la que manda el cliente; la mitad de entrada usa una derivada (hash) para no chocar con otras operaciones de la cuenta que recibe. Una transferencia **no se revierte** (la otra mitad vive en otra cuenta): se compensa
  con otra en sentido contrario. Las compuertas por cuenta se toman siempre en el mismo orden para que dos transferencias cruzadas no se bloqueen.
- **Incentivo: comision del cajero.** El cajero se queda con un **porcentaje de lo que carga**, en fichas, con tope por carga y por dia. Los jugadores no reciben bonus por una carga.
  **Fase B, decidido: la paga la casa** (acredita fichas nuevas a quien cargo, como una carga normal de backoffice), **2 % para el cajero** y 0 % para el jefe por defecto (configurables en `Cashiers`), con tope de 1.000
  por carga y 10.000 por dia UTC. El jugador recibe el monto completo; la comision es un movimiento aparte ("Comision por cargas"). **La decision se guarda antes de pagar** (`CommissionRecord`, una por carga): un reintento
  paga exactamente lo mismo y nunca dos veces, aunque entre medio haya cambiado el tope diario. La casa emite fichas, pero acotadas: el backoffice ve el total emitido. Es una carga con tope diario "blando" entre
  instancias (dentro de una instancia se serializa por persona).
- **Cobro (extraccion): el camino inverso, con el protocolo que ya existe** (fase C, implementada: ver el detalle al final de la decision). El jugador pide retirar: sus fichas se **reservan** (mismo protocolo reservar → resolver → liquidar de los juegos). Su
  cajero (o backoffice) **aprueba y paga**; al liquidar se debitan las fichas del jugador y se **transfieren al cajero**, que cierra el ciclo. Si no se aprueba o vence, la reserva se **libera** y las
  fichas vuelven al jugador. La doble aprobacion para montos altos sigue siendo de la fase 5.
- **Retiros (fase C), como quedo:** el jugador pide retirar (`POST /wallet/withdrawals`, con `Idempotency-Key`, minimo 10) y sus fichas pasan a una **reserva** (`gameId = "withdrawal"`; no se pueden jugar). Lo atiende su **padre en el arbol al pedir**
  (un jugador → su cajero; un cajero → su jefe; el jefe no tiene padre: ver abajo). Si el padre **cobra**, una nueva operacion de la Wallet, `ReservationPaidOut`, cierra la reserva y las fichas pasan a la cuenta del padre **en una sola transaccion** (la mitad
  de salida cierra la reserva y la de entrada es un `ChipsTransferred`; no se revierte). Si **rechaza**, el jugador **cancela** o **vence** el plazo (72 h por defecto, `Cashiers:WithdrawalExpiryHours`), la reserva se libera y las fichas vuelven.
  El estado del pedido (`WithdrawalRequest`) se **deriva de la reserva**: un pedido "pendiente" cuya reserva ya no esta abierta se marca vencido al leerlo, sin necesidad de un mensaje de vuelta. Un cobro y una cancelacion a la vez tienen un solo
  ganador (el otro recibe 409), porque ambos pasan por la misma reserva. **Quien no tiene cajero** (un jugador suelto, o un **jefe de cajeros**) lo atiende el **backoffice**, que al cobrar devuelve las fichas **a la casa** (liquidacion de la
  reserva sin premio). Un cajero solo ve y atiende los retiros de **su** gente (403 si no), ni siquiera su jefe.
- **Por fases, cada una con tests (unitarios + Testcontainers) y su ADR de cierre:** **A** rol, jerarquia, cuenta del cajero y `Transfer`; **B** comision; **C** pedidos de retiro y aprobacion.

## Consecuencias
- La Wallet gana una operacion (transferencia) y, con ella, el riesgo de dos cuentas en una transaccion: probado con concurrencia cruzada (30 transferencias a la vez en ambos sentidos: la suma de fichas no cambia), idempotencia
  y saldos que cierran. Las cuentas de cajeros y jefes reciben tambien las fichas de bienvenida como cualquier jugador (si molesta, se saca para esos roles).
- **Nombres, con minimo de datos:** el cajero ve el nombre de usuario de su gente (`HierarchyNode.DisplayName`, tomado del token cuando esa persona usa la API) y de nadie mas; solo se guarda de quienes estan en el arbol y no se muestra fuera de la jurisdiccion. Hasta que la persona usa la app se ve el id abreviado.
- Un cajero sin fichas no puede cargar: es una restriccion buscada, pero necesita una pantalla para que backoffice le recargue.
- La jerarquia de un solo padre es simple pero rigida: cambiar a un jugador de cajero es una operacion de backoffice auditada; subcajeros (niveles) quedan para despues.
- Esto es un juego con fichas ficticias. Si algun dia las fichas tuvieran valor real, habria que revisar aspectos legales y de prevencion de lavado antes de seguir.
- Queda para despues: niveles de cajeros (cajero de cajeros), referidos entre jugadores, limites configurables por cajero y reportes de liquidacion.
