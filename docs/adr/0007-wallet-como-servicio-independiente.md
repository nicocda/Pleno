# ADR 0007 - La Wallet como servicio independiente

- **Estado:** aceptada (primer paso de la fase 4; los juegos se separan en el siguiente)
- **Fecha:** 2026-10-09

## Contexto
El sistema era un monolito modular: un solo proceso (`Casino.Api`) con todos los modulos, una base de datos y un esquema. Los modulos ya se
hablaban solo por mensajes (`Casino.Contracts`) y ninguno referenciaba el codigo de otro, asi que el limite estaba listo. Faltaba
separar de verdad: procesos, datos y puerta de entrada. Se empieza por la Wallet porque es la pieza con el limite mas claro (solo recibe
ordenes y publica hechos) y la que mas valor tiene aislar: es la dueña de las fichas.

## Decision
- **Servicio propio** `Casino.WalletService`: su propio proceso, su `Program` y su despliegue. `Casino.Api` ya no tiene el modulo de la Wallet.
- **Una base de datos por servicio** (`walletdb` para la Wallet, `usersdb` para el host principal). Ningun servicio lee ni escribe las tablas
  de otro; lo que necesita de otro se lo pide por mensajes o por HTTP. Consecuencia: **los datos locales anteriores no se migran** (un
  entorno de laboratorio parte de cero; Keycloak conserva los usuarios y cada uno se vuelve a dar de alta al entrar).
- **Gateway con YARP en `Casino.Api`.** El navegador sigue hablando con una sola URL. La API se queda con usuarios, juegos y tiempo real
  (SignalR) y **reenvia** `/wallet/**` y `/backoffice/wallet/**` a la Wallet con un proxy inverso (YARP, de Microsoft). Las rutas y destinos
  son configuracion (`ReverseProxy`); con Aspire el destino `http://wallet` se resuelve por descubrimiento de servicios.
- **Cada servicio valida el token por su cuenta** (misma validacion que antes, compartida en `Casino.Hosting`): no confian en que el
  gateway ya lo hizo, asi que llegar directo a un servicio no es un atajo. Los roles tambien se exigen en cada servicio.
- **La topologia de RabbitMQ la declaran todos.** `MessagingTopology` (en `Casino.Hosting`) es la unica fuente de verdad de colas,
  intercambios y uniones, y cada servicio la declara completa al arrancar (es idempotente). Asi el orden de arranque no importa y un
  mensaje nunca se pierde por publicarse antes de que exista la cola de quien lo consume (hay una prueba: un jugador que entra mientras
  la Wallet no esta corriendo recibe su cuenta y sus fichas cuando la Wallet arranca). Cada servicio solo ESCUCHA sus propias colas.
- **`Casino.Hosting`** concentra lo comun a todos los servicios: autenticacion JWT, JSON, la base propia (Marten) y la mensajeria
  (Wolverine con inbox y outbox durables). Cada servicio lo compone con lo suyo.
- **Pruebas distribuidas.** Las pruebas de integracion ya no levantan "la API completa" sino un **cluster** (`CasinoCluster`): cada servicio
  es un host real e independiente, con su base y su conexion a RabbitMQ; las llamadas HTTP entran por el gateway y el **proxy real** las
  reenvia a la Wallet (solo el "cable" entre ambos es memoria en vez de red). Lo que cruza servicios viaja por RabbitMQ de verdad.
  Se agregaron pruebas de lo que cambia al separar: bases independientes, token validado en cada servicio, la Wallet caida no tumba al resto
  (el gateway responde 5xx y lo demas sigue), la Wallet no expone rutas ajenas y el orden de arranque.

## Consecuencias
- **Aislamiento de fallos:** si la Wallet cae, el gateway responde error en lo que depende de ella y usuarios, juegos y tiempo real siguen
  funcionando. Las apuestas quedan en cola y se reservan al volver (outbox/inbox durables).
- **Mas piezas para operar:** otro proceso, otra base y un salto de red mas en cada llamada a la Wallet. Es el precio de poder escalar,
  desplegar y fallar por separado. Las mediciones con k6 (ver `docs/pruebas-de-carga.md`) cuantifican ese costo.
- **Los hechos de la Wallet siguen llegando por RabbitMQ** a los juegos y al tiempo real; ese camino no cambio.
- **Rendimiento medido (k6, `docs/pruebas-de-carga.md`):** no hay diferencia medible entre el monolito y la Wallet separada en esta maquina
  (a 50 jugadores ambos cierran ~850 a 1.000 apuestas por minuto con una mediana de cobro de 1,7 a 3,5 s; el ruido entre corridas es mayor
  que cualquier diferencia). El techo es el mismo, asi que el cuello de botella no es el salto entre servicios: se investiga con trazas.
- **Validado en el sistema real (Aspire + navegador):** login, alta, saldo vía gateway, apuestas, saldo en vivo por SignalR (el hecho cruza
  Wallet → RabbitMQ → API → navegador) y verificacion pasan con los servicios separados (e2e de Playwright).
- **Pendiente (siguiente paso de la fase 4):** extraer tambien el motor de juegos, con su propia base, y volver a medir.
- **Pruebas mas lentas:** cada prueba levanta dos hosts en vez de uno. Si molesta, las que no cruzan el limite pueden levantar uno solo.
