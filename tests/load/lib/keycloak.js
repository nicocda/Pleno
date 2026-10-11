import http from 'k6/http';

// Acceso a Keycloak para las pruebas de carga. Solo desarrollo local: usa el cliente de pruebas `casino-tests`
// (login con usuario y contrasena) y, si se pasa la clave del administrador, crea jugadores sinteticos.

const json = { 'Content-Type': 'application/json' };

/** Token de acceso de un jugador (flujo de contrasena del cliente de pruebas). */
export function login(cfg, username, password) {
  const res = http.post(
    `${cfg.kcUrl}/realms/${cfg.realm}/protocol/openid-connect/token`,
    { grant_type: 'password', client_id: 'casino-tests', username, password },
    { tags: { name: 'kc_token' } },
  );
  if (res.status !== 200) {
    throw new Error(`No se pudo iniciar sesion como ${username}: HTTP ${res.status}`);
  }
  return res.json('access_token');
}

/** Token del administrador de Keycloak (realm master). La clave viene de una variable de entorno: nunca se escribe en el repo. */
function adminToken(cfg) {
  const res = http.post(
    `${cfg.kcUrl}/realms/master/protocol/openid-connect/token`,
    { grant_type: 'password', client_id: 'admin-cli', username: cfg.kcAdminUser, password: cfg.kcAdminPassword },
    { tags: { name: 'kc_admin_token' } },
  );
  if (res.status !== 200) {
    throw new Error(`No se pudo iniciar sesion como administrador de Keycloak: HTTP ${res.status}`);
  }
  return res.json('access_token');
}

/** Crea los jugadores sinteticos load-001..N (datos inventados, nunca reales). Es idempotente: si ya existen, no pasa nada. */
export function ensureLoadUsers(cfg, count) {
  const token = adminToken(cfg);
  const headers = { ...json, Authorization: `Bearer ${token}` };
  const names = [];
  for (let i = 1; i <= count; i += 1) {
    const username = `load-${String(i).padStart(3, '0')}`;
    names.push(username);
    const res = http.post(
      `${cfg.kcUrl}/admin/realms/${cfg.realm}/users`,
      JSON.stringify({
        username,
        email: `${username}@load.invalid`,
        firstName: 'Carga',
        lastName: String(i),
        enabled: true,
        emailVerified: true,
        credentials: [{ type: 'password', value: cfg.loadPassword, temporary: false }],
      }),
      { headers, tags: { name: 'kc_create_user' } },
    );
    if (res.status !== 201 && res.status !== 409) {
      throw new Error(`No se pudo crear ${username}: HTTP ${res.status}`);
    }
  }
  return names;
}
