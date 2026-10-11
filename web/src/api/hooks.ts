import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useApi } from "./ApiProvider";
import { ApiError } from "./client";
import type { Account, Withdrawal, LoadChipsResult, CashierMe, CashierTransfer, HierarchyLevel, HierarchyNode, JurisdictionMember, CreateTableBody, TableCreated, TableRules, TableState, TableSummary, BlackjackBet, BlackjackTable, BlackjackTableState, CrashBet, CrashCashOut, CrashState, GameInfo, SlotsSettings, SlotsSettingsBody, SlotsSettingsHistoryItem, SlotsSettingsPreview, CreditFilter, CreditHistory, MovementsPage, AuditEntry, CreditResult, FairnessInfo, Me, Paytable, PlaceBetBody, PlacedBet, Round, Spin, UserSummary } from "./types";

export const queryKeys = {
  spinsAll: ["spins"] as const,
  spins: (limit: number) => ["spins", limit] as const,
  paytable: ["paytable"] as const,
  me: ["me"] as const,
  account: ["account"] as const,
  rounds: (limit: number) => ["rounds", limit] as const,
  roundsAll: ["rounds"] as const,
  fairness: ["fairness"] as const,
  adminUsers: ["admin", "users"] as const,
  adminAudit: ["admin", "audit"] as const,
  adminCredits: (filter: CreditFilter) => ["admin", "credits", filter] as const,
  adminCreditsAll: ["admin", "credits"] as const,
  movements: ["movements"] as const,
  games: ["games"] as const,
  crashState: ["crash", "state"] as const,
  crashBets: ["crash", "bets"] as const,
  blackjackTables: ["blackjack", "tables"] as const,
  blackjackTable: (tableId: string) => ["blackjack", "table", tableId] as const,
  tablesOf: (gameId: string) => ["tables", gameId] as const,
  tableList: (gameId: string) => ["tables", gameId, "list"] as const,
  tableRules: (gameId: string) => ["tables", gameId, "rules"] as const,
  table: (gameId: string, tableId: string) => ["tables", gameId, "table", tableId] as const,
  slotsSettings: ["admin", "slots-settings"] as const,
  slotsSettingsHistory: ["admin", "slots-settings-history"] as const,
  adminAccount: (userId: string) => ["admin", "account", userId] as const,
  hierarchy: ["admin", "hierarchy"] as const,
  cashierMe: ["cashier", "me"] as const,
  cashierMembers: ["cashier", "members"] as const,
  cashierTransfers: ["cashier", "transfers"] as const,
  myWithdrawals: ["withdrawals", "mine"] as const,
  cashierWithdrawals: ["cashier", "withdrawals"] as const,
  adminWithdrawals: ["admin", "withdrawals"] as const,
};

export const BACKOFFICE_ROLE = "backoffice";
export const CASHIER_ROLE = "cashier";
export const HEAD_CASHIER_ROLE = "head_cashier";

/** Quien tiene rol de cajero o de jefe de cajeros. */
export const isCashierRole = (roles: readonly string[] | undefined): boolean => !!roles && (roles.includes(CASHIER_ROLE) || roles.includes(HEAD_CASHIER_ROLE));

/** Datos del usuario autenticado. Llamarlo tambien da de alta al jugador la primera vez. */
export function useMe() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.me, queryFn: () => api.get<Me>("/me"), staleTime: 60_000 });
}

/**
 * Cuenta de fichas. La cuenta de un jugador nuevo se abre de forma asincrona (por mensajes): durante un instante
 * puede responder 404. Se reintenta hasta 30 veces, una por segundo, antes de rendirse.
 */
export function useAccount() {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.account,
    queryFn: () => api.get<Account>("/wallet/me"),
    retry: (count, error) => error instanceof ApiError && error.status === 404 && count < 30,
    retryDelay: 1_000,
  });
}

export function useRounds(limit = 20) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.rounds(limit), queryFn: () => api.get<Round[]>(`/games/roulette/rounds?limit=${limit}`) });
}

export function useFairness() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.fairness, queryFn: () => api.get<FairnessInfo>("/fairness/me") });
}

/** Rota las seeds: revela la server seed activa (para poder verificar jugadas) y empieza un par nuevo. */
export function useRotateSeeds() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<FairnessInfo>("/fairness/me/rotate"),
    onSuccess: (info) => queryClient.setQueryData(queryKeys.fairness, info),
  });
}

export function usePlaceBet() {
  const api = useApi();
  return useMutation({
    mutationFn: ({ body, idempotencyKey }: { body: PlaceBetBody; idempotencyKey: string }) =>
      api.post<PlacedBet>("/games/roulette/bets", body, { "Idempotency-Key": idempotencyKey }),
  });
}

/** Una ronda concreta. Si `enabled`, se consulta cada 3 s hasta que cierre: es el respaldo por si se pierde el aviso en vivo. */
export function useRound(betId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: ["round", betId],
    queryFn: () => api.get<Round>(`/games/roulette/rounds/${betId}`),
    enabled: betId !== null,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "Settled" || status === "Rejected" || status === "Voided" ? false : 3_000;
    },
  });
}

/** Jugadores registrados. Solo responde a quien tenga el rol de administrador. */
export function useAdminUsers(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.adminUsers, queryFn: () => api.get<UserSummary[]>("/backoffice/users"), enabled });
}

/** Cuenta de cualquier jugador (solo administrador). */
export function useAdminAccount(userId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.adminAccount(userId ?? ""),
    queryFn: () => api.get<Account>(`/backoffice/wallet/users/${userId}`),
    enabled: userId !== null,
  });
}

/** Carga fichas a un jugador. La clave de idempotencia la pone quien llama: reintentar el mismo envio no duplica la carga. */
export function useCreditChips() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, amount, idempotencyKey }: { userId: string; amount: number; idempotencyKey: string }) =>
      api.post<CreditResult>(`/backoffice/wallet/users/${userId}/credit`, { amount }, { "Idempotency-Key": idempotencyKey }),
    onSuccess: (_result, { userId }) =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.adminAccount(userId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.adminAudit }),
        queryClient.invalidateQueries({ queryKey: queryKeys.adminCreditsAll }),
      ]),
  });
}

/** Registro de auditoria: quien cargo fichas, a quien y cuando (las mas recientes primero). */
export function useAdminAudit(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.adminAudit, queryFn: () => api.get<AuditEntry[]>("/backoffice/wallet/audit?limit=50"), enabled });
}

/** Tabla de pagos y retorno de la tragamonedas (informacion publica). */
export function usePaytable() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.paytable, queryFn: () => api.get<Paytable>("/games/slots/paytable"), staleTime: 5 * 60_000 });
}

export function useSpins(limit = 10) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.spins(limit), queryFn: () => api.get<Spin[]>(`/games/slots/spins?limit=${limit}`) });
}

export function usePlaceSpin() {
  const api = useApi();
  return useMutation({
    mutationFn: ({ stake, idempotencyKey }: { stake: number; idempotencyKey: string }) =>
      api.post<PlacedBet>("/games/slots/spins", { stake }, { "Idempotency-Key": idempotencyKey }),
  });
}

/** Un giro concreto. Se consulta cada segundo hasta que cierre: asi se ven los rodillos apenas se sortean y es el respaldo si se pierde el aviso en vivo. */
export function useSpin(betId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: ["spin", betId],
    queryFn: () => api.get<Spin>(`/games/slots/spins/${betId}`),
    enabled: betId !== null,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "Settled" || status === "Rejected" || status === "Voided" ? false : 1_000;
    },
  });
}

/** Extracto de movimientos del jugador, de a paginas (cursor). Se pide "cargar mas" para ver lo anterior. */
export function useMovements(pageSize = 30) {
  const api = useApi();
  return useInfiniteQuery({
    queryKey: queryKeys.movements,
    initialPageParam: null as number | null,
    queryFn: ({ pageParam }) => api.get<MovementsPage>(`/wallet/me/movements?limit=${pageSize}${pageParam === null ? "" : `&before=${pageParam}`}`),
    getNextPageParam: (last) => last.nextBefore ?? undefined,
  });
}

/** Historial general de cargas (solo administrador): filtros por jugador y fechas, paginado, con el total del filtro. */
export function useAdminCredits(filter: CreditFilter, enabled: boolean, pageSize = 25) {
  const api = useApi();
  return useInfiniteQuery({
    queryKey: queryKeys.adminCredits(filter),
    initialPageParam: null as string | null,
    enabled,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({ limit: String(pageSize) });
      if (filter.userId) params.set("userId", filter.userId);
      if (filter.from) params.set("from", filter.from);
      if (filter.to) params.set("to", filter.to);
      if (pageParam) params.set("before", pageParam);
      return api.get<CreditHistory>(`/backoffice/wallet/credits?${params.toString()}`);
    },
    getNextPageParam: (last) => last.nextBefore ?? undefined,
  });
}

/** Ajustes vigentes de la tragamonedas (solo administrador). */
export function useSlotsSettings(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.slotsSettings, queryFn: () => api.get<SlotsSettings>("/backoffice/games/slots/settings"), enabled });
}

/** Prueba una tabla SIN guardarla: devuelve el retorno y la frecuencia de premio, o por que no sirve. */
export function useSlotsPreview(body: SlotsSettingsBody | null) {
  const api = useApi();
  return useQuery({
    queryKey: ["admin", "slots-preview", body],
    queryFn: () => api.post<SlotsSettingsPreview>("/backoffice/games/slots/settings/preview", body),
    enabled: body !== null,
    staleTime: Infinity,
    placeholderData: (previous) => previous,
  });
}

/** Publica una version nueva de la tabla. Falla con 409 si otra persona publico antes. */
export function usePublishSlotsSettings() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: SlotsSettingsBody) => api.post<SlotsSettings>("/backoffice/games/slots/settings", body),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.slotsSettings }),
        queryClient.invalidateQueries({ queryKey: queryKeys.slotsSettingsHistory }),
        queryClient.invalidateQueries({ queryKey: queryKeys.paytable }),
      ]),
  });
}

export function useSlotsSettingsHistory(enabled: boolean) {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.slotsSettingsHistory,
    queryFn: () => api.get<SlotsSettingsHistoryItem[]>("/backoffice/games/slots/settings/history?limit=25"),
    enabled,
  });
}

/** El catalogo de juegos habilitados en el servidor. El lobby lista esto: agregar o quitar un juego no toca el front. */
export function useGames() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.games, queryFn: () => api.get<GameInfo[]>("/games"), staleTime: 60_000 });
}

/**
 * El estado de Crash: la ronda en curso, mi apuesta y las ultimas explosiones. Se consulta cada segundo (respaldo) y los hechos en vivo que
 * llegan por SignalR lo refrescan al instante. `offsetMs` es cuanto adelanta el reloj del servidor al del navegador, para dibujar el
 * multiplicador con la hora del servidor.
 */
export function useCrashState() {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.crashState,
    queryFn: async () => {
      const sentAt = Date.now();
      const state = await api.get<CrashState>("/games/crash/state");
      const receivedAt = Date.now();
      // Se supone que la respuesta tardo lo mismo en ir que en volver: la hora del servidor corresponde a la mitad del viaje.
      return { ...state, offsetMs: Date.parse(state.serverNow) - (sentAt + receivedAt) / 2 };
    },
    // Con una apuesta en juego se consulta mas seguido: asi el boton de retiro aparece apenas despega el cohete aunque se pierda un aviso en vivo.
    refetchInterval: (query) => (query.state.data?.myBet?.inPlay ? 250 : 1_000),
  });
}

export function usePlaceCrashBet() {
  const api = useApi();
  return useMutation({
    mutationFn: ({ stake, autoCashOut, idempotencyKey }: { stake: number; autoCashOut: number | null; idempotencyKey: string }) =>
      api.post<{ betId: string; roundId: string }>("/games/crash/bets", { stake, autoCashOut }, { "Idempotency-Key": idempotencyKey }),
  });
}

export function useCrashCashOut() {
  const api = useApi();
  return useMutation({ mutationFn: (betId: string) => api.post<CrashCashOut>(`/games/crash/bets/${betId}/cashout`) });
}

export function useCrashBets(limit = 10) {
  const api = useApi();
  return useQuery({ queryKey: [...queryKeys.crashBets, limit], queryFn: () => api.get<CrashBet[]>(`/games/crash/bets?limit=${limit}`) });
}

/** Las mesas de Blackjack y como esta cada una. Se refresca cada 3 s mientras se mira la lista. */
export function useBlackjackTables() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.blackjackTables, queryFn: () => api.get<BlackjackTable[]>("/games/blackjack/tables"), refetchInterval: 3_000 });
}

/**
 * El estado de una mesa de Blackjack: la mano en curso y los asientos. Como en Crash, se consulta cada segundo (respaldo) y los hechos en vivo
 * lo refrescan al instante. `offsetMs` es cuanto adelanta el reloj del servidor al del navegador, para las cuentas regresivas.
 */
export function useBlackjackTable(tableId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.blackjackTable(tableId ?? ""),
    enabled: tableId !== null,
    queryFn: async () => {
      const sentAt = Date.now();
      const state = await api.get<BlackjackTableState>(`/games/blackjack/tables/${tableId}`);
      const receivedAt = Date.now();
      return { ...state, offsetMs: Date.parse(state.serverNow) - (sentAt + receivedAt) / 2 };
    },
    refetchInterval: 1_000,
  });
}

export function usePlaceBlackjackBet(tableId: string) {
  const api = useApi();
  return useMutation({
    mutationFn: ({ stake, idempotencyKey }: { stake: number; idempotencyKey: string }) =>
      api.post<{ betId: string; roundId: string; alreadyPlaced: boolean }>(`/games/blackjack/tables/${tableId}/bets`, { stake }, { "Idempotency-Key": idempotencyKey }),
  });
}

/** Pedir carta o plantarse con la apuesta propia. */
export function useBlackjackAction() {
  const api = useApi();
  return useMutation({ mutationFn: ({ betId, action }: { betId: string; action: "hit" | "stand" }) => api.post<BlackjackBet>(`/games/blackjack/bets/${betId}/${action}`) });
}

// ---- Mesas entre jugadores (Uno y los que vengan): los mismos endpoints para todo juego, bajo /games/{gameId} ----

/** Las reglas basicas del juego (jugadores y entrada permitidos), para armar el formulario de crear mesa. */
export function useTableRules(gameId: string) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.tableRules(gameId), queryFn: () => api.get<TableRules>(`/games/${gameId}/rules`), staleTime: 5 * 60_000 });
}

/** Las mesas abiertas y las propias en curso. Se refresca cada 3 s (respaldo del aviso en vivo `tableChanged`). */
export function useTables(gameId: string) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.tableList(gameId), queryFn: () => api.get<TableSummary[]>(`/games/${gameId}/tables`), refetchInterval: 3_000 });
}

/**
 * Una mesa vista por MI asiento. Se consulta cada segundo (respaldo) y el aviso en vivo la refresca al instante. `offsetMs` es cuanto adelanta el
 * reloj del servidor al del navegador, para las cuentas regresivas. Un 4xx (no existe, es privada) no se reintenta ni se sigue consultando.
 */
export function useTable<G = unknown>(gameId: string, tableId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: queryKeys.table(gameId, tableId ?? ""),
    enabled: tableId !== null,
    queryFn: async () => {
      const sentAt = Date.now();
      const state = await api.get<TableState<G>>(`/games/${gameId}/tables/${tableId}`);
      const receivedAt = Date.now();
      return { ...state, offsetMs: Date.parse(state.serverNow) - (sentAt + receivedAt) / 2 };
    },
    refetchInterval: (query) => (query.state.error instanceof ApiError && query.state.error.status >= 400 && query.state.error.status < 500 ? false : 1_000),
  });
}

/** Crea una mesa. La clave de idempotencia la pone quien llama: reintentar el mismo envio no crea otra mesa. */
export function useCreateTable(gameId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ body, idempotencyKey }: { body: CreateTableBody; idempotencyKey: string }) =>
      api.post<TableCreated>(`/games/${gameId}/tables`, body, { "Idempotency-Key": idempotencyKey }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) }),
  });
}

/** Sentarse en una mesa (con el codigo si es privada). */
export function useJoinTable(gameId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ tableId, code }: { tableId: string; code?: string }) => api.post<void>(`/games/${gameId}/tables/${tableId}/join`, code ? { code } : undefined),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) }),
  });
}

/** Sentarse en una mesa privada con solo su codigo: el servidor dice cual es la mesa. */
export function useJoinByCode(gameId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => api.post<{ tableId: string }>(`/games/${gameId}/tables/join`, { code }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) }),
  });
}

/** Irse de una mesa que todavia no empezo. */
export function useLeaveTable(gameId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (tableId: string) => api.post<void>(`/games/${gameId}/tables/${tableId}/leave`),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) }),
  });
}

/** Agregar o quitar un bot (solo el dueño, antes de empezar). */
export function useTableBots(gameId: string, tableId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  const settled = () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) });
  const add = useMutation({ mutationFn: () => api.post<void>(`/games/${gameId}/tables/${tableId}/bots`), onSettled: settled });
  const remove = useMutation({ mutationFn: () => api.delete<void>(`/games/${gameId}/tables/${tableId}/bots`), onSettled: settled });
  return { add, remove };
}

/** Empezar la partida (solo el dueño). */
export function useStartTable(gameId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (tableId: string) => api.post<void>(`/games/${gameId}/tables/${tableId}/start`),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.tablesOf(gameId) }),
  });
}

/** Una jugada propia del juego (el cuerpo lo define cada juego). */
export function useTableAction<A = unknown>(gameId: string, tableId: string) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (action: A) => api.post<void>(`/games/${gameId}/tables/${tableId}/action`, action),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.table(gameId, tableId) }),
  });
}

// ---- Cajeros y jerarquia de cargas ----

/** Mi lugar en la jerarquia. 404 si el backoffice todavia no me asigno uno. */
export function useCashierMe(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.cashierMe, queryFn: () => api.get<CashierMe>("/cashier/me"), enabled, retry: false });
}

/** La gente de mi jurisdiccion directa, con su saldo. */
export function useCashierMembers(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.cashierMembers, queryFn: () => api.get<JurisdictionMember[]>("/cashier/members"), enabled });
}

/** Mis ultimas cargas. */
export function useCashierTransfers(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.cashierTransfers, queryFn: () => api.get<CashierTransfer[]>("/cashier/transfers?limit=20"), enabled });
}

/** Cargar fichas desde mi saldo a alguien de mi jurisdiccion. */
export function useLoadChips() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ toUserId, amount, idempotencyKey }: { toUserId: string; amount: number; idempotencyKey: string }) =>
      api.post<LoadChipsResult>("/cashier/transfers", { toUserId, amount }, { "Idempotency-Key": idempotencyKey }),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.cashierMembers }),
        queryClient.invalidateQueries({ queryKey: queryKeys.cashierTransfers }),
        queryClient.invalidateQueries({ queryKey: queryKeys.account }),
        queryClient.invalidateQueries({ queryKey: queryKeys.movements }),
      ]),
  });
}

/** El arbol completo de cargas (solo backoffice). */
export function useHierarchy(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.hierarchy, queryFn: () => api.get<HierarchyNode[]>("/backoffice/wallet/hierarchy"), enabled });
}

/** Pone a alguien en el arbol con un nivel y un padre (solo backoffice). */
export function useAssignHierarchy() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, level, parentUserId }: { userId: string; level: HierarchyLevel; parentUserId: string | null }) =>
      api.put<HierarchyNode>(`/backoffice/wallet/hierarchy/${userId}`, { level, parentUserId }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.hierarchy }),
  });
}

// ---- Retiros ----

/** Mis pedidos de retiro. */
export function useMyWithdrawals() {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.myWithdrawals, queryFn: () => api.get<Withdrawal[]>("/wallet/withdrawals"), refetchInterval: 5_000 });
}

/** Pide retirar: las fichas quedan apartadas hasta que las cobre mi cajero. */
export function useRequestWithdrawal() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ amount, idempotencyKey }: { amount: number; idempotencyKey: string }) =>
      api.post<Withdrawal>("/wallet/withdrawals", { amount }, { "Idempotency-Key": idempotencyKey }),
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: queryKeys.myWithdrawals }), queryClient.invalidateQueries({ queryKey: queryKeys.account })]),
  });
}

export function useCancelWithdrawal() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post<Withdrawal>(`/wallet/withdrawals/${id}/cancel`),
    onSettled: () => Promise.all([queryClient.invalidateQueries({ queryKey: queryKeys.myWithdrawals }), queryClient.invalidateQueries({ queryKey: queryKeys.account })]),
  });
}

/** Retiros pendientes de la gente a mi cargo (cajero o jefe). */
export function useCashierWithdrawals(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.cashierWithdrawals, queryFn: () => api.get<Withdrawal[]>("/cashier/withdrawals"), enabled, refetchInterval: 5_000 });
}

/** Cobrar (las fichas pasan a mi cuenta) o rechazar (vuelven al jugador) un retiro. */
export function useResolveWithdrawal(scope: "cashier" | "backoffice") {
  const api = useApi();
  const queryClient = useQueryClient();
  const base = scope === "cashier" ? "/cashier/withdrawals" : "/backoffice/wallet/withdrawals";
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: "pay" | "reject" }) => api.post<Withdrawal>(`${base}/${id}/${action}`),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: scope === "cashier" ? queryKeys.cashierWithdrawals : queryKeys.adminWithdrawals }),
        queryClient.invalidateQueries({ queryKey: queryKeys.account }),
        queryClient.invalidateQueries({ queryKey: queryKeys.movements }),
      ]),
  });
}

/** Retiros sin cajero (jugador sin cajero o jefe de cajeros): los atiende el backoffice. */
export function useAdminWithdrawals(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: queryKeys.adminWithdrawals, queryFn: () => api.get<Withdrawal[]>("/backoffice/wallet/withdrawals"), enabled, refetchInterval: 5_000 });
}
