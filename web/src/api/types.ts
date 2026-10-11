// Contratos de la API (JSON en camelCase). Las fichas son siempre enteros: nunca decimales.

/** Un jugador visto desde el panel de administracion: solo id y fecha de alta (no hay datos personales). */
export interface UserSummary {
  userId: string;
  registeredAt: string;
}

/** Una anotacion del registro de auditoria del backoffice. */
export interface AuditEntry {
  action: string;
  actorUserId: string;
  targetUserId: string;
  amount: number;
  transactionId: string;
  occurredAt: string;
}

/** Que paso con las fichas disponibles. */
export type MovementKind = "WelcomeBonus" | "Credit" | "Stake" | "Prize" | "Refund" | "Reversal" | "TransferIn" | "TransferOut" | "Commission" | "Withdrawal" | "WithdrawalReturned";

export type WithdrawalStatus = "Pending" | "Paid" | "Rejected" | "Cancelled" | "Expired";

/** Un pedido de retiro: las fichas quedan apartadas hasta que lo cobra el cajero (o se cancela, rechaza o vence). */
export interface Withdrawal {
  id: string;
  playerUserId: string;
  /** Nombre de usuario del jugador: solo lo recibe quien atiende el retiro. */
  playerName?: string | null;
  amount: number;
  status: WithdrawalStatus;
  createdAt: string;
  resolvedAt: string | null;
}

/** Nivel en la jerarquia de cargas (los mismos nombres que los roles). */
export type HierarchyLevel = "player" | "cashier" | "head_cashier";

/** Un nodo del arbol de cargas: de quien depende cada usuario (lo arma el backoffice). */
export interface HierarchyNode {
  userId: string;
  level: HierarchyLevel;
  /** Nombre de usuario (si ya lo usó la API estando en el árbol). */
  displayName?: string | null;
  parentUserId: string | null;
  updatedAt: string;
}

/** Mi lugar en la jerarquia (cajero o jefe). */
export interface CashierMe {
  userId: string;
  level: HierarchyLevel;
  parentUserId: string | null;
  /** Lo que cobro por cada carga, en milesimas (20 = 2 %). Lo paga la casa. */
  commissionPermille: number;
}

/** El resultado de una carga: la transaccion y la comision que cobro quien cargo (0 si no cobra o llego al tope). */
export interface LoadChipsResult {
  transactionId: string;
  isDuplicate: boolean;
  commission: number;
}

/** Alguien de mi jurisdiccion directa; el saldo es null si todavia no abrio su cuenta. */
export interface JurisdictionMember {
  userId: string;
  level: HierarchyLevel;
  /** Nombre de usuario para distinguirlo; null hasta que use la app. Solo lo ve quien lo tiene a cargo. */
  displayName?: string | null;
  available: number | null;
  reserved: number | null;
}

/** Una carga que hice. */
export interface CashierTransfer {
  targetUserId: string;
  /** Solo viene si el destinatario sigue a mi cargo. */
  targetName?: string | null;
  amount: number;
  transactionId: string;
  occurredAt: string;
}

/** Un movimiento del saldo: `delta` con signo y el saldo disponible justo despues. */
export interface Movement {
  version: number;
  at: string;
  kind: MovementKind;
  delta: number;
  balanceAfter: number;
  reference: string | null;
  /** Juego de la apuesta, el premio o la devolucion; null si no aplica (cargas) o es anterior a que se guardara. */
  gameId?: string | null;
}

export interface MovementsPage {
  items: Movement[];
  /** Cursor de la pagina siguiente; null si no hay mas. */
  nextBefore: number | null;
}

/** Pagina del historial general de cargas, con el total de lo que cumple el filtro (no solo de la pagina). */
export interface CreditHistory {
  items: AuditEntry[];
  nextBefore: string | null;
  totalAmount: number;
  count: number;
}

export interface CreditFilter {
  userId: string | null;
  /** Inicio (inclusive) del rango, ISO. */
  from: string | null;
  /** Fin (exclusivo) del rango, ISO. */
  to: string | null;
}

export interface CreditResult {
  transactionId: string;
  isDuplicate: boolean;
}

export interface Me {
  userId: string;
  displayName: string | null;
  roles: string[];
  accountId: string;
  registeredAt: string;
}

export interface Account {
  accountId: string;
  userId: string;
  available: number;
  reserved: number;
  version: number;
  openReservations: Record<string, number>;
}

export type RoundStatus = "Placed" | "Resolved" | "Settled" | "Rejected" | "Voided";

export interface Round {
  betId: string;
  status: RoundStatus;
  betType: RouletteBetType;
  selection: number[];
  /** Total apostado en la tirada. */
  stake: number;
  bets: BetLine[];
  pairId: string;
  nonce: number;
  winningNumber: number | null;
  payout: number | null;
  failureReason: string | null;
  placedAt: string;
}

export type RouletteBetType =
  | "Straight"
  | "Split"
  | "Street"
  | "Trio"
  | "Corner"
  | "FirstFour"
  | "SixLine"
  | "Dozen"
  | "Column"
  | "Red"
  | "Black"
  | "Even"
  | "Odd"
  | "Low"
  | "High";

/** Una apuesta dentro de una tirada. */
export interface BetLine {
  betType: RouletteBetType;
  selection: number[];
  stake: number;
}

/** Una tirada: una o varias apuestas que comparten el mismo numero sorteado. */
export interface PlaceBetBody {
  bets: BetLine[];
}

export interface PlacedBet {
  betId: string;
  status: RoundStatus;
  nonce: number;
  commitment: string;
  clientSeed: string;
  alreadyPlaced: boolean;
}

/** Quien decide el resultado de un juego: el servidor (verificable) o la propia pagina. */
export type GameResolution = "Server" | "Client";

/** La ficha de un juego en el catalogo del servidor: lo minimo que todo juego declara. El lobby se arma con esto. */
export interface GameInfo {
  id: string;
  name: string;
  tagline: string;
  /** Ruta del front donde se juega. */
  route: string;
  glyph: string;
  resolution: GameResolution;
}

/** Un hecho en vivo que un juego de ronda compartida difunde a todos los jugadores. `data` es propio de cada juego. */
export interface GameEvent {
  game: string;
  kind: string;
  data: unknown;
  at: string;
}

/** Una ronda de Crash. El punto de explosion y la semilla son null hasta que explota. */
export interface CrashRound {
  id: string;
  phase: "Betting" | "Running" | "Crashed" | "Aborted";
  commitment: string;
  openedAt: string;
  bettingEndsAt: string;
  startedAt: string | null;
  crashedAt: string | null;
  /** En centesimas (250 = x2,50). */
  crashPoint: number | null;
  serverSeed: string | null;
  edgePermille: number;
  growthPerSecond: number;
}

export interface CrashBet {
  betId: string;
  roundId: string;
  status: RoundStatus;
  stake: number;
  autoCashOut: number | null;
  /** Reservada y todavia sin resultado: se puede retirar. */
  inPlay: boolean;
  /** Multiplicador (centesimas) en el que se retiro. */
  cashedOutAt: number | null;
  payout: number | null;
  failureReason: string | null;
  placedAt: string;
}

export interface CrashState {
  serverNow: string;
  growthPerSecond: number;
  minStake: number;
  maxStake: number;
  round: CrashRound | null;
  myBet: CrashBet | null;
  history: CrashRound[];
}

export interface CrashCashOut {
  betId: string;
  multiplier: number;
  payout: number;
}

/** Un paso de un giro: lo que mostraban los rodillos, cuanto paga esa combinacion y el multiplicador de la cascada (x1 el giro inicial). */
export interface SlotsStep {
  reels: string[];
  pay: number;
  multiplier: number;
}

/** Un giro de tragamonedas. */
export interface Spin {
  betId: string;
  status: RoundStatus;
  stake: number;
  /** Nombre del simbolo de cada rodillo (vacio hasta que se sortea). */
  reels: string[];
  /** Giro inicial y cascadas, en orden. Vacio en giros anteriores a las cascadas (alcanza con `reels`). */
  steps?: SlotsStep[];
  multiplier: number | null;
  payout: number | null;
  pairId: string;
  nonce: number;
  failureReason: string | null;
  placedAt: string;
}

export interface PaytableSymbol {
  name: string;
  weight: number;
  triplePayout: number;
}

export interface LeadingPay {
  symbol: string;
  count: number;
  payout: number;
}

/** Tabla de pagos publica de la tragamonedas. */
export interface Paytable {
  reels: number;
  symbols: PaytableSymbol[];
  leadingPays: LeadingPay[];
  minStake: number;
  maxStake: number;
  totalWeight: number;
  returnToPlayerPercent: number;
  hitRatePercent: number;
  /** Multiplicador de cada paso de la cascada (el primero es el giro inicial). */
  cascadeMultipliers: number[];
  /** Un premio encadena solo si paga al menos esto. */
  cascadeMinPay: number;
}

/** Una tabla de pagos de la tragamonedas tal como la ve el administrador. */
export interface SlotsSettingsView {
  symbols: PaytableSymbol[];
  leadingPays: LeadingPay[];
  maxStake: number;
  totalWeight: number;
  returnToPlayerPercent: number;
  hitRatePercent: number;
}

/** Ajustes vigentes (con su version) y los de la configuracion, a los que se puede volver. */
export interface SlotsSettings {
  version: number;
  current: SlotsSettingsView;
  baseline: SlotsSettingsView;
}

/** Lo que se envia para probar o publicar una tabla. `baseVersion` es la version que se estaba mirando. */
export interface SlotsSettingsBody {
  symbols: PaytableSymbol[];
  leadingPays: LeadingPay[];
  maxStake: number;
  baseVersion: number;
}

export interface SlotsSettingsPreview {
  valid: boolean;
  error: string | null;
  returnToPlayerPercent: number | null;
  hitRatePercent: number | null;
  totalWeight: number | null;
}

export interface SlotsSettingsHistoryItem {
  version: number;
  changedBy: string;
  changedAt: string;
  returnToPlayerPercent: number;
  hitRatePercent: number;
  maxStake: number;
  symbols: number;
}

export interface RetiredPair {
  pairId: string;
  commitment: string;
  serverSeed: string;
  clientSeed: string;
  betsPlayed: number;
}

export interface FairnessInfo {
  userId: string;
  active: { pairId: string; commitment: string; clientSeed: string; nextNonce: number };
  pendingBets: number;
  retired: RetiredPair[];
}

// Avisos en vivo (SignalR)
export interface BalanceNotice {
  available: number;
  reserved: number;
  version: number;
}

export interface RoundClosedNotice {
  betId: string;
  game: string;
  status: "Settled" | "Rejected" | "Voided";
  winningNumber: number | null;
  stake: number;
  payout: number;
  failureReason: string | null;
}

/** Una mesa de Blackjack. `phase` es null si todavia no hay ninguna mano. */
export interface BlackjackTable {
  id: string;
  name: string;
  minStake: number;
  maxStake: number;
  maxSeats: number;
  phase: BlackjackPhase | null;
  players: number;
}

export type BlackjackPhase = "Betting" | "Playing" | "Finished" | "Aborted";
export type BlackjackHand = "Waiting" | "Playing" | "Stood" | "Bust" | "Blackjack";
export type BlackjackResult = "Blackjack" | "Win" | "Push" | "Lose" | "Bust";

export interface BlackjackRound {
  id: string;
  tableId: string;
  phase: BlackjackPhase;
  commitment: string;
  openedAt: string;
  /** null hasta la primera apuesta. */
  bettingEndsAt: string | null;
  finishedAt: string | null;
  seatCount: number;
  /** `hiddenCards` son las que estan boca abajo (la tapada no viaja hasta que se da vuelta). */
  dealer: { cards: number[]; hiddenCards: number };
  activeSeat: number | null;
  turnEndsAt: string | null;
  serverSeed: string | null;
}

/** Un asiento: la mano es publica pero no dice de quien es. `betId` solo viene en el propio. */
export interface BlackjackSeat {
  seat: number | null;
  stake: number;
  cards: number[];
  hand: BlackjackHand;
  result: BlackjackResult | null;
  payout: number | null;
  status: RoundStatus;
  mine: boolean;
  betId: string | null;
}

export interface BlackjackTableState {
  serverNow: string;
  table: BlackjackTable;
  bettingSeconds: number;
  turnSeconds: number;
  round: BlackjackRound | null;
  seats: BlackjackSeat[];
}

export interface BlackjackBet {
  betId: string;
  roundId: string;
  tableId: string;
  status: RoundStatus;
  stake: number;
  seat: number;
  cards: number[];
  hand: BlackjackHand;
  result: BlackjackResult | null;
  payout: number | null;
  failureReason: string | null;
  placedAt: string;
}

// Mesas entre jugadores (Uno y los que vengan): contrato comun de la plataforma de mesas.
export type TableStatus = "Open" | "Playing" | "Finished" | "Cancelled";

export interface TableRules {
  minPlayers: number;
  maxPlayers: number;
  minBuyIn: number;
  maxBuyIn: number;
}

/** Una mesa en el listado. `mine`: estoy sentado. */
export interface TableSummary {
  id: string;
  gameId: string;
  name: string;
  buyIn: number;
  maxPlayers: number;
  players: number;
  bots: number;
  status: TableStatus;
  mine: boolean;
  isPrivate: boolean;
  createdAt: string;
}

export interface CreateTableBody {
  buyIn: number;
  maxPlayers: number;
  isPrivate?: boolean;
  name?: string;
}

export interface TableCreated {
  tableId: string;
  /** Solo si la mesa es privada. */
  joinCode: string | null;
  alreadyCreated: boolean;
}

/** Un asiento tal como lo ve cualquiera: sin identidad ("Jugador 2", "Bot 3"). `ready`: las fichas ya estan confirmadas; `away`: ausente, lo juega un bot. */
export interface TableSeat {
  seat: number;
  name: string;
  isBot: boolean;
  mine: boolean;
  ready: boolean;
  away: boolean;
  payout: number | null;
}

/** La mesa vista por mi asiento. `game` es la vista propia de cada juego (null hasta que empieza). */
export interface TableState<G = unknown> {
  serverNow: string;
  id: string;
  gameId: string;
  name: string;
  status: TableStatus;
  buyIn: number;
  minPlayers: number;
  maxPlayers: number;
  isOwner: boolean;
  isPrivate: boolean;
  joinCode: string | null;
  commitment: string;
  serverSeed: string | null;
  seats: TableSeat[];
  mySeat: number | null;
  turnSeat: number | null;
  turnEndsAt: string | null;
  game: G | null;
  payouts: number[] | null;
}

// Uno
export type UnoEventKind = "start" | "play" | "draw" | "pass" | "skipped" | "penalty";

export interface UnoEvent {
  seat: number;
  kind: UnoEventKind;
  card: number | null;
  color: number | null;
  count: number | null;
}

/** Lo que ve mi asiento: mi mano, lo jugable ahora, cuantas cartas tiene cada uno y la carta de arriba. */
export interface UnoView {
  you: number | null;
  hand: number[];
  playable: number[];
  players: { seat: number; cards: number }[];
  top: number;
  topColor: number;
  drawCount: number;
  current: number;
  direction: 1 | -1;
  drawnCard: number | null;
  winner: number;
  events: UnoEvent[];
}

/** La jugada propia de Uno. */
export type UnoAction = { type: "play"; card: number; color?: number } | { type: "draw" } | { type: "pass" };

// Poker (Texas Hold'em de una mano por mesa)
export type PokerStreet = "preflop" | "flop" | "turn" | "river" | "done";
export type PokerEventKind = "blind" | "fold" | "check" | "call" | "bet" | "raise" | "flop" | "turn" | "river" | "win" | "win_showdown";
export type PokerCategoryName = "HighCard" | "Pair" | "TwoPair" | "ThreeOfAKind" | "Straight" | "Flush" | "FullHouse" | "FourOfAKind" | "StraightFlush";
export type PokerActionName = "call" | "check" | "fold" | "raise";

export interface PokerEvent {
  /** -1 en los eventos de calle (flop, turn, river). */
  seat: number;
  kind: PokerEventKind;
  amount: number | null;
  allIn: boolean;
}

export interface PokerPlayer {
  seat: number;
  stack: number;
  /** Lo puesto en esta ronda de apuestas. */
  bet: number;
  folded: boolean;
  allIn: boolean;
  acted: boolean;
  /** null salvo las propias y, al showdown, las de quienes siguen en la mano. */
  cards: number[] | null;
  cardCount: number;
}

export interface PokerShowdownEntry {
  seat: number;
  cards: number[];
  category: PokerCategoryName;
  won: boolean;
}

/** Lo que ve mi asiento de la mano. */
export interface PokerView {
  you: number | null;
  hand: number[];
  board: number[];
  street: PokerStreet;
  pot: number;
  players: PokerPlayer[];
  dealer: number;
  smallBlindSeat: number;
  bigBlindSeat: number;
  current: number;
  currentBet: number;
  toCall: number;
  minRaiseTo: number;
  maxRaiseTo: number;
  actions: PokerActionName[];
  showdown: PokerShowdownEntry[];
  winnings: number[];
  done: boolean;
  events: PokerEvent[];
}

/** La jugada propia de Poker. `to`: monto TOTAL al que se sube en esta ronda. */
export type PokerAction = { type: "fold" } | { type: "check" } | { type: "call" } | { type: "raise"; to: number };

// Truco
export type TrucoEventKind = "start" | "play" | "truco" | "retruco" | "vale4" | "envido" | "real_envido" | "falta_envido" | "quiero" | "no_quiero" | "mazo" | "baza" | "parda" | "envido_result" | "hand_end";

export interface TrucoEvent {
  seat: number;
  kind: TrucoEventKind;
  card: number | null;
  value: number | null;
  a: number | null;
  b: number | null;
}

/** Un canto que espera respuesta: un truco (`level` 1 a 3) o un envido (con la cadena de cantos). */
export interface TrucoPending {
  kind: "truco" | "envido";
  caller: number;
  level: number;
  calls: string[];
}

/** Lo que ve mi asiento: mis cartas y mi envido, la mesa de esta ronda, el marcador, el canto pendiente y las jugadas legales ahora. */
/** Una carta jugada en la mesa y quien la jugo. */
export interface TrucoPlay {
  seat: number;
  card: number;
}

export interface TrucoView {
  you: number | null;
  hand: number[];
  envidoPoints: number | null;
  opponentCards: number;
  table: TrucoPlay[];
  /** Quien gano cada mano de la ronda (el asiento, o -1 si fue parda). El nombre viene del servidor (`bazas`). */
  bazas: number[];
  scores: number[];
  target: number;
  mano: number;
  handNo: number;
  current: number;
  trucoLevel: number;
  pending: TrucoPending | null;
  actions: TrucoActionType[];
  winner: number;
  events: TrucoEvent[];
}

export type TrucoActionType = "play" | "truco" | "retruco" | "vale4" | "envido" | "real_envido" | "falta_envido" | "quiero" | "no_quiero" | "mazo";

/** La jugada propia de Truco: las cartas se juegan con `play`. */
export type TrucoAction = { type: "play"; card: number } | { type: Exclude<TrucoActionType, "play"> };
