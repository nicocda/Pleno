# Demo publica con devtunnel

Muestra el MVP a quien no esta en tu red. Es un modo **apagado por defecto**: solo se activa con el secreto `Lan:PublicUrl`. Solo se expone el **front (5173)**: Vite reenvia `/api` a la API y `/connect`, `/identity`,
`/signin-google` y `/.well-known` al servicio de identidad, que siguen en localhost (misma idea que [red-local.md](red-local.md), pero el HTTPS lo pone el tunel).

## Una sola vez
1. Tunel con URL fija y acceso anonimo: `devtunnel create pleno-demo --allow-anonymous` y `devtunnel port create pleno-demo -p 5173 --protocol http`.
   La URL queda `https://pleno-demo-5173.<region>.devtunnels.ms` (la region sale del id del tunel; hoy `brs`).
2. Secretos (nada de esto se versiona):
   ```
   dotnet user-secrets set "Lan:PublicUrl" "https://pleno-demo-5173.brs.devtunnels.ms" --project src/Casino.AppHost
   dotnet user-secrets set "Parameters:demo-users-password" "<al menos 12 caracteres, solo para la demo>" --project src/Casino.AppHost
   ```
   **La contraseña de la demo es obligatoria**: las de los usuarios de desarrollo ([autenticacion.md](autenticacion.md)) son publicas, y con el tunel abierto cualquiera podria entrar como `backoffice1`. Con `Lan:PublicUrl` y sin esa
   contraseña el AppHost **no arranca**. Con ella, `jugador1`, `jugador2`, `backoffice1`, `jefe1` y `cajero1` usan **todos esa contraseña** (tambien los que ya existian en la base de una corrida anterior: se les cambia y se cierran sus sesiones).
3. Si vas a usar Google en la demo, agregar `https://pleno-demo-5173.brs.devtunnels.ms/signin-google` a las URI de redireccion autorizadas de la app OAuth (ver [autenticacion.md](autenticacion.md)).

## Cada demo
1. `aspire run` (Docker Desktop corriendo).
2. `devtunnel host pleno-demo` (dejarlo abierto).
3. Fichas para el usuario de la demo: `python scripts/dev-jerarquia.py` con `--password "<la de la demo>"` o entrar como `backoffice1` a `/admin` y acreditar fichas a `jugador1`.
4. Abrir la URL del tunel y entrar con `jugador1` y la contraseña de la demo.

Volver al modo normal: `dotnet user-secrets remove "Lan:PublicUrl" --project src/Casino.AppHost`. Cuando termine la demo, cortar `devtunnel host` (o `devtunnel delete pleno-demo`).

## Que NO se expone
- **`/identity/dev/*`** (el buzon de correos de desarrollo, con los enlaces de verificacion y recuperacion): el proxy del front responde 404 aunque alguien conozca la ruta.
- Los puertos de la API, los servicios, Postgres, RabbitMQ y pgAdmin siguen solo en localhost; solo existe el 5173 a traves del tunel.
- **Registro abierto y sin SMTP:** cualquiera puede crear una cuenta desde la pantalla de entrada, pero sin envio de correo no puede verificar el email (el buzon de desarrollo no se expone), asi que no podra entrar. Para la demo se usan los usuarios sembrados.

## Limites
- Es el servidor de desarrollo de Vite y el servicio de identidad en `Development`: sirve para una demo, no para carga.
- Funciona mientras esta PC este prendida con Docker y `aspire run`.
