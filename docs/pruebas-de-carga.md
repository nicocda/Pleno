# Pruebas de carga con k6

Miden cuanto aguanta el sistema con muchos jugadores a la vez y sirven para comparar **antes y despues** de cambios de arquitectura
(por ejemplo, separar la Wallet y los juegos en servicios, fase 4). Estan en `tests/load/`.

## Que miden
Cada jugador virtual (VU) repite: apostar (ruleta con 1 a 3 apuestas por tirada, o tragamonedas) y consultar hasta que la ronda cierra.

| Metrica | Que es |
| --- | --- |
| **Aceptar apuesta / giro** | Cuanto tarda la API en responder `202`. Solo es el trayecto de entrada: la apuesta todavia no se resolvio. |
| **Cobrada de punta a punta** | Desde que la API acepta hasta que la ronda queda `Settled`: API → cola → Wallet reserva → juego sortea → cola → Wallet liquida. Es la que mas cambia al separar servicios. |
| **Apuestas sin cerrar a tiempo** | Rondas que no llegaron a un estado final en 20 s. Tiene que ser 0. |
| **Checks** | Aceptacion de cada apuesta y, al final, que **el saldo cierra** (ver abajo). |

Al terminar se verifica, por cada jugador, que **no quede ninguna reserva abierta** y que **la suma de los movimientos del extracto
sea exactamente el saldo disponible**. Si el sistema perdiera o duplicara fichas bajo carga, esta prueba falla.

## Como correrla
Requisitos: `k6` instalado y el sistema levantado (`aspire start`).

```
k6 run -e PLAYERS=20 -e VUS=20 -e RAMP=10s -e DURATION=60s -e POLL_MS=50 -e LABEL=monolito -e COMMIT=$(git rev-parse --short HEAD) tests/load/casino.js
```

| Variable | Por defecto | Para que |
| --- | --- | --- |
| `PLAYERS` / `VUS` | 10 / igual | Jugadores distintos y jugadores virtuales simultaneos |
| `RAMP` / `DURATION` | 15s / 60s | Subida gradual y tiempo sostenido |
| `SLOTS_RATIO` | 0.3 | Proporcion de giros de tragamonedas (el resto, ruleta) |
| `MAX_STAKE` | 5 | Fichas maximas por apuesta |
| `TOPUP` | 20000 | Fichas que el backoffice le carga a cada jugador antes de empezar |
| `POLL_MS` | 100 | Cada cuanto se consulta si cerro la ronda (la medicion de cobro tiene esa resolucion: usar 50 para afinar) |
| `BASE_URL` / `KC_URL` | `http://localhost:5188` / `http://localhost:8080` | Donde estan la API (o el gateway) y Keycloak |
| `LABEL` / `COMMIT` | `prueba` / `desconocido` | Se imprimen y van al archivo de resultados |
| `KC_ADMIN_PASSWORD` | vacio | **Opcional.** Con la clave del administrador de Keycloak se crean los jugadores sinteticos `load-001..N` (datos inventados). Sin ella se usan `jugador1` y `jugador2` |

La clave del administrador es un secreto: se pasa por variable de entorno en el momento (por ejemplo, tomandola de los user-secrets del
AppHost) y **nunca se escribe en el repositorio**. Los resultados crudos van a `tests/load/results/` (ignorados por git).

El script usa el cliente `casino-tests` del realm (login con usuario y contrasena, **solo desarrollo**) y el usuario `backoffice1` para
cargar fichas. Todo es local: no apuntar esto a un ambiente real.

## Como leer los numeros
- Mirar **mediana (med), p95 y p99**: el promedio esconde los casos lentos. p95 = el 95 % de las apuestas tardo eso o menos.
- **Hay ruido.** Todo corre en una misma maquina (k6, Docker, Keycloak, la API, el antivirus, otras compilaciones). Dos corridas iguales
  pueden diferir mucho. Para comparar, repetir cada escenario **al menos 3 veces** con la maquina quieta y comparar las medianas de las corridas.
- Las peticiones "fallidas" (~0,2 %) son los `404` de `GET /wallet/me` mientras se espera que se abra la cuenta de un jugador nuevo
  durante la preparacion; no son errores de la carga.
- Conviene medir un escalon que **no sature** (20 jugadores) y uno que **si** (50): el segundo muestra donde se acumula la cola.

## Linea base: monolito (antes de separar servicios)
Commit `b68f4a9` (`dev`). Un solo proceso `Casino.Api` con todos los modulos, una base de datos, RabbitMQ y Redis, en el portatil de desarrollo.
60 s sostenidos, 30 % tragamonedas, `POLL_MS=50`. Tiempos en milisegundos.

| Escenario | Aceptar apuesta (med / p95 / p99) | Cobrada de punta a punta, ruleta (med / p95 / p99) | Apuestas cerradas | Saldos |
| --- | --- | --- | --- | --- |
| 20 jugadores, corrida 1 | 27 / 142 / 379 | 177 / 428 / 3.375 | 1.777 | cierran |
| 20 jugadores, corrida 2 | 56 / 173 / 305 | 362 / 791 / 973 | 1.526 | cierran |
| 50 jugadores | 84 / 353 / 936 | 1.723 / 2.503 / 6.375 | 1.537 | cierran |

Lectura: con 20 jugadores el sistema responde con holgura (cobro de ~0,2 a 0,4 s). Con 50 **se satura**: el cobro de punta a punta sube a
~1,7 s de mediana y se cierran menos apuestas que con 20, lo que indica que el cuello esta en el trayecto por las colas (la propia
aceptacion de la apuesta sigue rapida). Ningun escenario perdio ni duplico fichas ni dejo rondas sin cerrar.

## Despues de separar la Wallet (ADR 0007)
Misma prueba, mismos escenarios, contra el sistema con la Wallet como servicio aparte (commit `214c873`: `Casino.WalletService` con su
base, gateway YARP en `Casino.Api`). Tambien se **volvio a medir el monolito** (`dev` actual) el mismo dia, porque entre momentos distintos
la maquina rinde muy distinto. Tiempos en milisegundos (med / p95 / p99); "cerradas" = apuestas cobradas en la corrida de 60 s.

| Escenario | Monolito (1ra medicion) | Monolito (remedido) | Wallet separada |
| --- | --- | --- | --- |
| 20 jugadores, cobro de punta a punta | 177 / 428 / 3.375 y 362 / 791 / 973 | 1.456 / 3.479 / 4.923 | 359 / 727 / 987 |
| 20 jugadores, apuestas cerradas | 1.777 y 1.526 | 598 | 1.447 |
| 50 jugadores, cobro de punta a punta | 1.723 / 2.503 / 6.375 | 2.691 / 5.692 / 7.847 y 3.351 / 6.354 / 8.525 | 3.259 / 8.063 / 9.501, 3.425 / 6.154 / 6.394 y 3.542 / 5.390 / 5.648 |
| 50 jugadores, apuestas cerradas | 1.537 | 1.005 y 855 | 852, 862 y 851 |
| Aceptar apuesta, 50 jugadores (mediana) | 84 | 135 y 154 | 133, 138 y 146 |
| Saldos | cierran | cierran | cierran |

**Lectura (honesta):**
- **No se nota diferencia entre el monolito y la Wallet separada.** A 50 jugadores, ambos se mueven en el mismo rango (mediana de cobro de
  1,7 a 3,5 s y de 850 a 1.000 apuestas cerradas por minuto). Separar no mejoro ni empeoro de forma medible el rendimiento en esta maquina.
- **El ruido es mayor que cualquier diferencia entre arquitecturas.** El mismo monolito con 20 jugadores dio una mediana de cobro de 177 ms
  en un momento y de 1.456 ms en otro (8 veces). Cualquier diferencia menor que eso no se puede atribuir a la arquitectura. Para decidir
  algo fino hay que medir con la maquina quieta y repetir muchas veces (o medir en un ambiente dedicado).
- **El techo es el mismo con las dos arquitecturas (~850 a 1.000 apuestas por minuto)**: el cuello de botella no es el salto entre
  servicios sino algo que ambos comparten (Postgres, RabbitMQ, el procesamiento de mensajes o la CPU de la maquina, donde ademas corre k6).
  Es el siguiente misterio a investigar: las **trazas de OpenTelemetry de punta a punta** (siguiente item de la fase 4) muestran en que paso
  se va el tiempo.
- **Arranque en frio:** con bases recien creadas, la primera apuesta puede tardar mas de 30 s (compilacion JIT, generacion de codigo de
  Wolverine, creacion de tablas). Conviene "calentar" con una corrida corta antes de medir (las pruebas de extremo a extremo con navegador
  tambien pueden fallar la primera vez en un stack nuevo y pasar la segunda).
