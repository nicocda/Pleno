# ADR 0006 - Tragamonedas: un segundo juego sobre la misma arquitectura

- **Estado:** aceptada
- **Fecha:** 2026-10-09

## Contexto
Con la ruleta funcionando, hacia falta un segundo juego para comprobar que la Wallet, el nonce del servidor y el recorrido
reservar -> sortear -> liquidar sirven para mas de un juego, antes de separar servicios (fase 4).

## Decision
- **Reutilizacion total.** La tragamonedas usa sin cambios la Wallet (`ReserveStake`, `RoundResolved`...), el generador de seeds y nonces
  (`FairnessService`) y los estados de `RoundStatus`. Solo se agrego lo propio del juego: tabla de pagos, giro y endpoints.
- **Un mensaje, varios juegos.** Los hechos de la Wallet traen solo el `BetId`. El manejador los entrega a cada juego y cada uno
  busca esa apuesta en lo suyo: si no es suya, la ignora (sin alarma). Los ids de apuesta de cada juego se derivan con un prefijo
  distinto (`bet:` / `slots:`), asi que una misma `Idempotency-Key` en dos juegos no choca.
- **Tres rodillos independientes.** Cada rodillo es una cinta con simbolos de distinto peso; el sorteo consume un numero del flujo
  provably fair por rodillo (el mismo esquema que la ruleta, asi que el giro es recalculable con la seed revelada).
- **Pagos enteros.** Premio = apuesta x multiplicador entero. Triple de un simbolo, y premios chicos por racha desde la izquierda
  (la cereza). Por defecto: retorno de **96,14 %** y algun premio en ~34 de cada 100 giros, casi siempre chico.
- **Tabla y retorno configurables** (seccion `Slots` de `appsettings.json`): simbolos con peso y pago triple, premios por racha y
  apuesta maxima. El retorno se calcula **exacto con enteros** al arrancar y **una tabla con retorno mayor al 100 % (o 0) impide
  iniciar la API**: la casa nunca puede perder a la larga por un error de configuracion. La tabla y el retorno son publicos
  (`GET /games/slots/paytable`).
- **Front:** los rodillos son un modelo puro de tiempo (`ReelsModel`, sin dibujo) que frena de izquierda a derecha en lo que ya decidio
  el servidor, con un minimo de giro para el suspenso. El saldo y el resultado no se adelantan: se muestran cuando frena el ultimo
  rodillo (mismo `holdBalance()` que la ruleta). El giro se consulta cada segundo: ya trae los rodillos apenas se sortea, y el
  aviso en vivo solo apura la consulta. Con "reducir movimiento" los rodillos frenan al instante.
- **Sin pagina de verificacion propia (por ahora):** el calculo es verificable con la seed revelada y esta probado en el backend,
  pero `/verify` solo sabe de ruleta.

## Ajustes editables desde el panel de administracion
- La tabla de la configuracion es la **version 0**. El administrador (rol `backoffice`) puede **publicar versiones nuevas** desde `/admin/tragamonedas`:
  simbolos, pesos, premios, premios por racha y apuesta maxima. Cada version es un documento **solo-agregar** con quien (solo el id) y cuando.
- **Se prueba antes de guardar:** `POST .../settings/preview` calcula el retorno y la frecuencia de premio sin guardar nada y explica por que una
  tabla no sirve. Las reglas son las mismas que al arrancar: **el retorno nunca puede superar el 100 %** (con mas, la casa perderia a la larga).
- **Sin pisar cambios ajenos:** al publicar se envia la version que se estaba mirando; si otra persona publico antes (o a la vez) la respuesta es 409.
- **Cada giro guarda la version de la tabla** con la que se sorteo, asi se puede recalcular mas adelante aunque la tabla cambie.
- La tabla vigente se cachea unos segundos por instancia (no se consulta la base en cada giro): un cambio puede tardar ese tiempo en llegar
  a las demas instancias de la API. Un giro ya colocado se sortea con la tabla vigente al momento de sortear; el tope de apuesta se valida al colocarlo.

## Consecuencias
- Un segundo juego costo poco porque el recorrido de dinero y azar esta separado del juego: valida la arquitectura antes de la fase 4.
- Cada juego repite el mismo esqueleto (servicio, giro/ronda, manejadores). Si aparece un tercero conviene extraer una base comun.
- Las probabilidades se pueden cambiar sin recompilar, pero cada cambio de tabla afecta el retorno: hay que mirar el valor que
  informa `/games/slots/paytable` (y el que valida el arranque).
- El historial de giros vive aparte del de ruleta (`GET /games/slots/spins`); un historial unificado queda para mas adelante.
