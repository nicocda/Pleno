# Probar desde otros dispositivos (red local)

Sirve para jugar en tiempo real desde dos o mas dispositivos (celular, otra PC) conectados a la misma red que la PC de desarrollo.

## Como funciona
Con `Lan:Host` configurado, el front se sirve por **HTTPS** en `https://<ip>:5173` y es el **unico puerto** que se abre a la red:
Vite reenvia `/api` a la API y `/realms` a Keycloak (que siguen escuchando solo en `localhost`). Asi no hay CORS ni URLs
distintas por dispositivo. HTTPS es obligatorio: el login (PKCE) usa `crypto.subtle`, que el navegador solo habilita en contextos seguros.
El certificado es autofirmado (`@vitejs/plugin-basic-ssl`): cada dispositivo muestra una advertencia que hay que aceptar una vez.

## Pasos
1. IP de la PC: `ipconfig` (IPv4 de la red WiFi/Ethernet, p. ej. `192.168.1.50`). Si cambia (DHCP), repetir el paso 2.
2. `dotnet user-secrets set "Lan:Host" 192.168.1.50 --project src/Casino.AppHost`
3. Firewall (PowerShell como administrador, una sola vez):
   `New-NetFirewallRule -DisplayName "Pleno web" -Direction Inbound -Protocol TCP -LocalPort 5173 -Action Allow -Profile Private`
   (la red debe estar marcada como **Privada** en Windows).
4. **Keycloak ya existente:** el realm solo se importa la primera vez. Para que acepte los redirect URIs de la red local, borra el volumen
   `keycloak-data` (se pierden los usuarios registrados) o agrega a mano en la consola de Keycloak, cliente `casino-web`, los
   *Valid redirect URIs* `https://<ip>:5173/*` y *Web origins* `https://<ip>:5173`.
5. `aspire run` y abrir `https://<ip>:5173` en cada dispositivo (aceptar la advertencia del certificado).

Para volver al modo normal: `dotnet user-secrets remove "Lan:Host" --project src/Casino.AppHost`.

## Limites
- Solo para desarrollo: el realm acepta `https://192.168.*`, `https://10.*` y `https://172.*` como redirect.
- Cada servicio valida el emisor contra la URL publica (`Authentication:Authority`) y lee las claves de Keycloak directo
  (`Authentication:MetadataAddress`, solo en modo red local).
- `Lan:Host` no es un secreto: se guarda en user-secrets solo para no versionar la IP de cada desarrollador.
