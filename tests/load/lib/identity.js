import http from 'k6/http';

// Acceso al servicio de identidad para las pruebas de carga. Solo desarrollo local: usa el cliente `casino-web` con usuario y contrasena.
// Los jugadores sinteticos load-001..N los siembra el propio servicio de identidad al arrancar (solo en Development) si se levanto el AppHost
// con `Identity:SeedLoadUsers=N` (ver docs/pruebas-de-carga.md): aca solo se calculan sus nombres.

/** Token de acceso de un jugador (grant de contrasena del cliente propio). */
export function login(cfg, username, password) {
  const res = http.post(
    `${cfg.identityUrl}/connect/token`,
    { grant_type: 'password', client_id: 'casino-web', username, password },
    { tags: { name: 'identity_token' } },
  );
  if (res.status !== 200) {
    throw new Error(`No se pudo iniciar sesion como ${username}: HTTP ${res.status}`);
  }
  return res.json('access_token');
}

/** Nombres de los jugadores sinteticos load-001..N (datos inventados, nunca reales). */
export function loadUserNames(count) {
  const names = [];
  for (let i = 1; i <= count; i += 1) {
    names.push(`load-${String(i).padStart(3, '0')}`);
  }
  return names;
}
