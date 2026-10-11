# Provably fair: especificacion del algoritmo

Este documento define el algoritmo con el detalle suficiente para reimplementarlo (por ejemplo, en JavaScript para la
pagina de verificacion) y comprobar cualquier jugada. La implementacion de referencia es
`src/Modules/Games/Fairness/FairRng.cs`.

## Ingredientes

| Nombre | Descripcion |
| --- | --- |
| `serverSeed` | 32 bytes aleatorios criptograficos, representados como **64 caracteres hex en minuscula**. Secreta mientras esta en uso. |
| `commitment` | `hex(SHA-256(UTF8(serverSeed)))`. Se publica **antes** de jugar. |
| `clientSeed` | Texto libre del jugador, de 1 a 64 caracteres. |
| `nonce` | Entero >= 0. Sube en 1 con cada apuesta que usa el mismo par de seeds. |

## Flujo de bytes

Se define una secuencia de bloques de 32 bytes indexados por `cursor` (0, 1, 2, ...):

```
bloque(cursor) = HMAC-SHA256( clave = UTF8(serverSeed),
                              mensaje = UTF8(clientSeed + ":" + nonce + ":" + cursor) )
```

`nonce` y `cursor` se escriben en decimal, sin ceros a la izquierda ni separadores de miles.

El flujo de bytes es la concatenacion `bloque(0) || bloque(1) || ...`. Los bytes se consumen **de a 4**, como un entero sin signo
de 32 bits **big-endian** (`uint32`). Cada bloque aporta 8 enteros; cuando se agota, se calcula el siguiente `cursor`.

## Entero uniforme en [0, max)

Para evitar el sesgo del modulo se aplica *rejection sampling*:

```
limite = floor(2^32 / max) * max
repetir:
    v = siguiente uint32 del flujo
    si v < limite: devolver v mod max
    (si no, se descarta v y se toma el siguiente)
```

`max` debe estar entre 1 y 2^31 - 1.

## Verificacion de una jugada

1. Al rotar la seed, el servidor revela `serverSeed`.
2. El jugador comprueba que `hex(SHA-256(UTF8(serverSeed)))` coincide con el `commitment` publicado antes de jugar.
3. Para cada apuesta con su `nonce`, recalcula el resultado: en la ruleta europea es el **primer** entero de `[0, 37)` del flujo.

## Vectores de prueba

Generados con una implementacion independiente en Node.js (no con el codigo de la plataforma):

```
serverSeed = 9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff
clientSeed = pleno-lab
commitment = 52ca56a3d81d3be381d594a5bca342bb63f6a3597776fed89138c9244fcbd2c8

nonce 0, max 37 (12 valores):  26,13,21,22,9,30,11,7,5,26,27,4
nonce 1, max 37 (12 valores):  32,26,19,14,34,27,23,28,23,1,27,19
nonce 2, max 37 (12 valores):  23,20,24,21,5,16,14,13,7,29,29,24
nonce 7, max 1500000000 (4):   544190317,69811089,683837891,298158533
nonce 9, max 37 (20 valores):  13,31,31,23,14,15,18,0,25,18,3,23,24,3,23,28,1,30,5,29
```

El ultimo vector consume mas de 32 bytes, asi que cubre el cambio de `cursor`.

## Por que el jugador no puede ser enganado

- El servidor no puede cambiar la seed despues de ver la apuesta: el `commitment` ya estaba publicado.
- El servidor no puede elegir el resultado: depende de `clientSeed` y `nonce`, que controla el jugador y el contador.
- El jugador no puede predecir resultados mientras la seed esta en uso: no conoce `serverSeed`.

## Gestion de seeds en la plataforma

- **Un par de seeds por usuario** (un stream de eventos por usuario). El servidor asigna el nonce de forma atomica (la version
  del stream impide dos asignaciones iguales) y es idempotente por apuesta: reintentar la misma apuesta devuelve el mismo nonce.
- **El cliente no controla ningun valor.** Ni el nonce ni la server seed se aceptan por la API. La client seed solo se fija al
  empezar un par: cambiarla a mitad de camino permitiria volver a una seed anterior, repetir resultados ya conocidos y apostar
  a lo que se sabe que va a salir. Para cambiarla hay que **rotar**.
- **Rotar** revela la server seed activa, empieza un par nuevo (nuevo compromiso, nonce desde 0) y esta **bloqueado mientras
  haya apuestas sin resolver**, porque revelar la seed expondria el resultado de una jugada en curso.
- **Cifrado en reposo:** la server seed se guarda cifrada con AES-256-GCM. El dato asociado (usuario + par) ata cada texto
  cifrado a su dueño. La clave maestra (`Fairness:MasterKey`, 32 bytes en base64) vive en user-secrets en local y en Key Vault
  en la nube; **nunca en el repo**. Perder la clave implica perder las seeds activas.
- Un par cerrado ya tiene su server seed revelada: sus jugadas se pueden verificar con datos publicos.

```
dotnet user-secrets set "Fairness:MasterKey" "<32 bytes aleatorios en base64>" --project src/Casino.GamesService
```
