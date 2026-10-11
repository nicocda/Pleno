import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { account, credit, isFinal, me, movements, placeRoulette, placeSlots, rouletteRound, slotsSpin, uuid } from './lib/casino.js';
import { ensureLoadUsers, login } from './lib/keycloak.js';

// Prueba de carga del casino: muchos jugadores apostando a la vez a la ruleta y a la tragamonedas.
// Mide cuanto tarda la API en ACEPTAR una apuesta y cuanto tarda en quedar COBRADA de punta a punta
// (API -> cola -> Wallet reserva -> juego sortea -> cola -> Wallet liquida), y al final comprueba que el saldo cierra.
// Como se corre y como se leen los resultados: docs/pruebas-de-carga.md

const env = (name, fallback) => (__ENV[name] !== undefined && __ENV[name] !== '' ? __ENV[name] : fallback);

const cfg = {
  baseUrl: env('BASE_URL', 'http://localhost:5188'),
  kcUrl: env('KC_URL', 'http://localhost:8080'),
  realm: env('REALM', 'casino'),
  kcAdminUser: env('KC_ADMIN_USER', 'admin'),
  kcAdminPassword: env('KC_ADMIN_PASSWORD', ''), // opcional: con ella se crean jugadores load-001..N; sin ella se usan los de desarrollo
  loadPassword: env('LOAD_PASSWORD', 'load-dev-only'), // contrasena de los jugadores sinteticos (solo local)
  backofficeUser: env('BACKOFFICE_USER', 'backoffice1'),
  backofficePassword: env('BACKOFFICE_PASSWORD', 'backoffice1-dev'),
  players: Number(env('PLAYERS', '10')),
  vus: Number(env('VUS', env('PLAYERS', '10'))),
  ramp: env('RAMP', '15s'),
  duration: env('DURATION', '60s'),
  slotsRatio: Number(env('SLOTS_RATIO', '0.3')),
  maxStake: Number(env('MAX_STAKE', '5')),
  topUp: Number(env('TOPUP', '20000')),
  pollEveryMs: Number(env('POLL_MS', '100')),
  settleTimeoutMs: Number(env('SETTLE_TIMEOUT_MS', '20000')),
  label: env('LABEL', 'prueba'),
  commit: env('COMMIT', 'desconocido'),
};

// Usuarios de desarrollo documentados en docs/autenticacion.md (publicos y triviales a proposito).
const DEV_PLAYERS = [
  { username: 'jugador1', password: 'jugador1-dev' },
  { username: 'jugador2', password: 'jugador2-dev' },
];

const settleMs = new Trend('settle_ms', true);
const finalBets = new Counter('bets_final');
const timeouts = new Counter('bets_timeout');
const accepted = new Rate('bet_accepted');

export const options = {
  scenarios: {
    players: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: cfg.ramp, target: cfg.vus },
        { duration: cfg.duration, target: cfg.vus },
        { duration: '5s', target: 0 },
      ],
      gracefulRampDown: '30s',
      gracefulStop: '30s',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    // Umbrales holgados: la idea es medir, no aprobar. Se ajustan cuando haya una linea base.
    checks: ['rate>0.99'],
    bet_accepted: ['rate>0.99'],
    bets_timeout: ['count<1'],
    'http_req_duration{name:roulette_place}': ['p(95)<5000'],
    'http_req_duration{name:slots_place}': ['p(95)<5000'],
    'settle_ms{game:roulette}': ['p(95)<15000'],
    'settle_ms{game:slots}': ['p(95)<15000'],
  },
};

/** Los jugadores de la prueba: sinteticos si hay clave de administrador, o los de desarrollo. */
function playerCredentials() {
  if (cfg.kcAdminPassword) {
    return ensureLoadUsers(cfg, cfg.players).map((username) => ({ username, password: cfg.loadPassword }));
  }
  return DEV_PLAYERS.slice(0, Math.max(1, Math.min(cfg.players, DEV_PLAYERS.length)));
}

export function setup() {
  const credentials = playerCredentials();
  const adminToken = login(cfg, cfg.backofficeUser, cfg.backofficePassword);
  const runId = uuid().slice(0, 8);

  const players = credentials.map(({ username, password }) => {
    const token = login(cfg, username, password);
    const profile = me(cfg, token); // el primer ingreso da de alta al jugador (y le abre la cuenta)
    check(profile, { 'GET /me': (r) => r.status === 200 });
    const userId = profile.json('userId');

    // La cuenta se abre por mensajes: puede tardar un instante.
    let ready = false;
    for (let i = 0; i < 60 && !ready; i += 1) {
      ready = account(cfg, token).status === 200;
      if (!ready) sleep(0.5);
    }
    if (!ready) throw new Error(`La cuenta de ${username} no se abrio a tiempo`);

    const funded = credit(cfg, adminToken, userId, cfg.topUp, `load-topup-${runId}-${username}`);
    check(funded, { 'carga de fichas': (r) => r.status === 200 });
    return { username, password, userId };
  });

  return { players, runId, startedAt: Date.now() };
}

// Estado por VU (cada VU tiene su propio entorno de ejecucion).
let session = null;

function tokenFor(player) {
  // El token dura 5 minutos: se renueva antes para que una prueba larga no falle por vencimiento.
  if (session === null || session.username !== player.username || Date.now() - session.at > 240000) {
    session = { username: player.username, token: login(cfg, player.username, player.password), at: Date.now() };
  }
  return session.token;
}

const ROULETTE_BETS = [
  () => ({ betType: 'Red', selection: [] }),
  () => ({ betType: 'Black', selection: [] }),
  () => ({ betType: 'Even', selection: [] }),
  () => ({ betType: 'Odd', selection: [] }),
  () => ({ betType: 'Low', selection: [] }),
  () => ({ betType: 'High', selection: [] }),
  () => ({ betType: 'Straight', selection: [Math.floor(Math.random() * 37)] }),
  () => ({ betType: 'Dozen', selection: [1 + Math.floor(Math.random() * 3)] }),
  () => ({ betType: 'Column', selection: [1 + Math.floor(Math.random() * 3)] }),
];

const randomStake = () => 1 + Math.floor(Math.random() * cfg.maxStake);

function rouletteTurn(token) {
  const count = 1 + Math.floor(Math.random() * 3); // de 1 a 3 apuestas en la misma tirada
  const bets = Array.from({ length: count }, () => ({ ...ROULETTE_BETS[Math.floor(Math.random() * ROULETTE_BETS.length)](), stake: randomStake() }));
  const placed = placeRoulette(cfg, token, bets);
  return { placed, poll: (id) => rouletteRound(cfg, token, id), game: 'roulette' };
}

function slotsTurn(token) {
  const placed = placeSlots(cfg, token, randomStake());
  return { placed, poll: (id) => slotsSpin(cfg, token, id), game: 'slots' };
}

export default function (data) {
  const player = data.players[(__VU - 1) % data.players.length];
  const token = tokenFor(player);

  const turn = Math.random() < cfg.slotsRatio ? slotsTurn(token) : rouletteTurn(token);
  const ok = turn.placed.status === 202;
  accepted.add(ok, { game: turn.game });
  check(turn.placed, { [`${turn.game}: apuesta aceptada (202)`]: () => ok });

  if (ok) {
    const betId = turn.placed.json('betId');
    const t0 = Date.now();
    let status = 'Placed';
    while (!isFinal(status) && Date.now() - t0 < cfg.settleTimeoutMs) {
      sleep(cfg.pollEveryMs / 1000);
      const res = turn.poll(betId);
      if (res.status === 200) status = res.json('status');
    }
    if (isFinal(status)) {
      settleMs.add(Date.now() - t0, { game: turn.game });
      finalBets.add(1, { game: turn.game, status });
    } else {
      timeouts.add(1, { game: turn.game });
    }
  }

  sleep(0.2 + Math.random() * 0.4); // el jugador piensa un momento
}

/** Al final: ninguna reserva queda abierta y el saldo cierra contra el extracto (la suma de los movimientos es el saldo disponible). */
export function teardown(data) {
  const startedAt = Date.now();
  for (const player of data.players) {
    const token = login(cfg, player.username, player.password);
    let state = account(cfg, token).json();
    // Las rondas en vuelo cierran de forma asincrona: se espera un poco a que no quede nada reservado.
    for (let i = 0; i < 60 && (state.reserved !== 0 || Object.keys(state.openReservations).length > 0); i += 1) {
      sleep(0.5);
      state = account(cfg, token).json();
    }

    let sum = 0;
    let before = null;
    do {
      const page = movements(cfg, token, before).json();
      sum += page.items.reduce((total, m) => total + m.delta, 0);
      before = page.nextBefore;
    } while (before !== null);

    check(state, {
      [`${player.username}: sin reservas abiertas`]: (s) => s.reserved === 0 && Object.keys(s.openReservations).length === 0,
      [`${player.username}: el saldo cierra contra el extracto`]: (s) => s.available === sum,
    });
  }
  console.log(`Verificacion de saldos: ${data.players.length} jugadores en ${Date.now() - startedAt} ms`);
}

const fmt = (value) => (value === undefined ? '-' : value.toFixed(1));

function trendLine(label, metric) {
  if (!metric) return null;
  const v = metric.values;
  return `${label.padEnd(38)} med ${fmt(v.med).padStart(8)}  p95 ${fmt(v['p(95)']).padStart(8)}  p99 ${fmt(v['p(99)']).padStart(8)}  max ${fmt(v.max).padStart(8)}  (ms)`;
}

export function handleSummary(data) {
  const m = data.metrics;
  const count = (name) => (m[name] ? m[name].values.count : 0);
  const lines = [
    '',
    `=== Resultados "${cfg.label}" (commit ${cfg.commit}) ===`,
    `Jugadores: ${cfg.players} | VUs: ${cfg.vus} | sostenido: ${cfg.duration} | tragamonedas: ${Math.round(cfg.slotsRatio * 100)}%`,
    '',
    trendLine('Aceptar apuesta (ruleta)', m['http_req_duration{name:roulette_place}']),
    trendLine('Aceptar giro (tragamonedas)', m['http_req_duration{name:slots_place}']),
    trendLine('Cobrada de punta a punta (ruleta)', m['settle_ms{game:roulette}']),
    trendLine('Cobrada de punta a punta (slots)', m['settle_ms{game:slots}']),
    '',
    `Apuestas cerradas: ${count('bets_final')} | sin cerrar a tiempo: ${count('bets_timeout')}`,
    `Peticiones HTTP: ${count('http_reqs')} (${m.http_reqs ? m.http_reqs.values.rate.toFixed(1) : 0}/s) | fallidas: ${m.http_req_failed ? (m.http_req_failed.values.rate * 100).toFixed(2) : 0}%`,
    `Checks: ${m.checks ? (m.checks.values.rate * 100).toFixed(2) : 0}% correctos`,
    '',
  ].filter((line) => line !== null);

  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  return {
    stdout: `${lines.join('\n')}\n`,
    [`results/${cfg.label}-${stamp}.json`]: JSON.stringify({ label: cfg.label, commit: cfg.commit, config: { ...cfg, kcAdminPassword: undefined }, metrics: data.metrics }, null, 2),
  };
}
