# Probar desde otros dispositivos (red local)

Sirve para jugar en tiempo real desde dos o mas dispositivos (celular, otra PC) conectados a la misma red que la PC de desarrollo.

## Como funciona
Con `Lan:Host` configurado, el front se sirve por **HTTPS** en `https://<ip>:5173` y es el **unico puerto** que se abre a la red:
Vite reenvia `/api` a la API y `/connect`, `/identity`, `/signin-google` y `/.well-known` al servicio de identidad (que siguen escuchando solo en `localhost`). Asi no hay CORS ni URLs
distintas por dispositivo. HTTPS es obligatorio para que el navegador acepte las cookies seguras del login con Google.
El certificado es autofirmado (`@vitejs/plugin-basic-ssl`): cada dispositivo muestra una advertencia que hay que aceptar una vez.

## Pasos
1. IP de la PC: `ipconfig` (IPv4 de la red WiFi/Ethernet, p. ej. `192.168.1.50`). Si cambia (DHCP), repetir el paso 2.
2. `dotnet user-secrets set "Lan:Host" 192.168.1.50 --project src/Casino.AppHost`
3. Firewall (PowerShell como administrador, una sola vez):
   `New-NetFirewallRule -DisplayName "Pleno web" -Direction Inbound -Protocol TCP -LocalPort 5173 -Action Allow -Profile Private`
   (la red debe estar marcada como **Privada** en Windows).
4. El servicio de identidad usa `https://<ip>:5173` como emisor de los tokens y como origen permitido (lo configura el AppHost solo). Si ya habias entrado antes, volve a iniciar sesion.
5. `aspire run` y abrir `https://<ip>:5173` en cada dispositivo (aceptar la advertencia del certificado).

Para volver al modo normal: `dotnet user-secrets remove "Lan:Host" --project src/Casino.AppHost`.

## Limites
- Solo para desarrollo: el servicio de identidad solo acepta volver a `https://<ip>:5173`.
- Cada servicio valida el emisor contra la URL publica (`Authentication:Authority`) y lee las claves del servicio de identidad directo
  (`Authentication:MetadataAddress`, solo en modo red local).
- `Lan:Host` no es un secreto: se guarda en user-secrets solo para no versionar la IP de cada desarrollador.
