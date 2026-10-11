import http from 'k6/http';

// Llamadas a la API del casino. Cada funcion devuelve la respuesta de k6 para que el script decida que medir.

const FINAL = ['Settled', 'Rejected', 'Voided'];

export const isFinal = (status) => FINAL.includes(status);

/** Identificador unico (v4). Alcanza para claves de idempotencia de una prueba. */
export function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r % 4) + 8).toString(16);
  });
}

const auth = (token, extra = {}) => ({ Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...extra });

export function me(cfg, token) {
  return http.get(`${cfg.baseUrl}/me`, { headers: auth(token), tags: { name: 'me' } });
}

export function account(cfg, token) {
  return http.get(`${cfg.baseUrl}/wallet/me`, { headers: auth(token), tags: { name: 'wallet_me' } });
}

export function movements(cfg, token, before) {
  const query = before === undefined || before === null ? 'limit=100' : `limit=100&before=${before}`;
  return http.get(`${cfg.baseUrl}/wallet/me/movements?${query}`, { headers: auth(token), tags: { name: 'movements' } });
}

/** Carga fichas a un jugador (solo backoffice). La clave hace la carga idempotente: repetir la prueba no duplica. */
export function credit(cfg, adminTokenValue, userId, amount, key) {
  return http.post(`${cfg.baseUrl}/backoffice/wallet/users/${userId}/credit`, JSON.stringify({ amount }), {
    headers: auth(adminTokenValue, { 'Idempotency-Key': key }),
    tags: { name: 'admin_credit' },
  });
}

export function placeRoulette(cfg, token, bets) {
  return http.post(`${cfg.baseUrl}/games/roulette/bets`, JSON.stringify({ bets }), {
    headers: auth(token, { 'Idempotency-Key': uuid() }),
    tags: { name: 'roulette_place' },
  });
}

export function rouletteRound(cfg, token, betId) {
  return http.get(`${cfg.baseUrl}/games/roulette/rounds/${betId}`, { headers: auth(token), tags: { name: 'roulette_poll' } });
}

export function placeSlots(cfg, token, stake) {
  return http.post(`${cfg.baseUrl}/games/slots/spins`, JSON.stringify({ stake }), {
    headers: auth(token, { 'Idempotency-Key': uuid() }),
    tags: { name: 'slots_place' },
  });
}

export function slotsSpin(cfg, token, betId) {
  return http.get(`${cfg.baseUrl}/games/slots/spins/${betId}`, { headers: auth(token), tags: { name: 'slots_poll' } });
}
