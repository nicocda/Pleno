import { AuthError, type AuthErrorCode } from "./identity";

export const UNREACHABLE_MESSAGE =
  "No pudimos conectar con el servidor de identidad. Si recién arrancó el sistema, esperá unos segundos y volvé a intentar.";

const MESSAGES: Record<AuthErrorCode, string> = {
  invalid_credentials: "Usuario o contraseña incorrectos. Si hiciste varios intentos, esperá unos minutos o recuperá tu contraseña.",
  email_not_verified: "Tu email todavía no está verificado. Revisá tu correo o pedí que te lo reenviemos.",
  unreachable: UNREACHABLE_MESSAGE,
  rate_limited: "Demasiados intentos. Esperá un minuto y volvé a intentar.",
  invalid_code: "No pudimos completar la entrada con Google. Intentá de nuevo.",
  alias_taken: "Ese alias ya está en uso. Elegí otro.",
  invalid_alias: "El alias tiene que tener entre 3 y 20 caracteres: letras, números, punto, guion o guion bajo.",
  invalid_email: "Ese email no es válido.",
  weak_password: "La contraseña tiene que tener al menos 10 caracteres.",
  invalid_token: "El enlace no es válido o ya venció. Pedí uno nuevo.",
  unknown: "Algo salió mal. Intentá de nuevo.",
};

/** Mensaje para el jugador a partir del codigo estable del error de identidad. */
export function authMessage(error: unknown): string {
  if (typeof error === "string" && error in MESSAGES) return MESSAGES[error as AuthErrorCode];
  return MESSAGES[error instanceof AuthError ? error.code : "unknown"];
}

export const RETURN_TO_KEY = "casino.returnTo";
