"""Arma el arbol de cajeros de DESARROLLO sobre una app local ya levantada (aspire run). Solo stdlib de Python 3.

    python scripts/dev-jerarquia.py [--api http://localhost:5188] [--keycloak http://localhost:8080] [--fichas 10000]

Hace, en orden (es idempotente: se puede correr de nuevo):
  1. Entra como cada usuario de desarrollo (cliente `casino-tests`, solo desarrollo) y llama a /me: asi se les abre la cuenta de fichas.
  2. Como `backoffice1` ubica el arbol: jefe1 (jefe de cajeros) -> cajero1 (cajero) -> jugador1 y jugador2 (jugadores).
  3. Le acredita fichas al jefe (`--fichas`, 0 para no hacerlo) con la carga normal del backoffice: de ahi en mas las fichas solo se mueven por el arbol.

Los usuarios y sus claves son los de desarrollo del realm (docs/autenticacion.md): NO usar contra un entorno real. No imprime tokens.
"""

import argparse
import json
import urllib.error
import urllib.parse
import urllib.request
import uuid

# Ids fijos del realm de desarrollo (src/Casino.AppHost/realms/casino-realm.json).
JEFE = "0a1b2c3d-0004-4000-8000-000000000004"
CAJERO = "0a1b2c3d-0005-4000-8000-000000000005"
JUGADORES = ["0a1b2c3d-0001-4000-8000-000000000001", "0a1b2c3d-0002-4000-8000-000000000002"]
DEV_USERS = {"backoffice1": "backoffice1-dev", "jefe1": "jefe1-dev", "cajero1": "cajero1-dev", "jugador1": "jugador1-dev", "jugador2": "jugador2-dev"}


def request(method, url, token=None, data=None, form=None, headers=None):
    all_headers = dict(headers or {})
    body = None
    if token:
        all_headers["Authorization"] = "Bearer " + token
    if data is not None:
        body = json.dumps(data).encode()
        all_headers["Content-Type"] = "application/json"
    if form is not None:
        body = urllib.parse.urlencode(form).encode()
        all_headers["Content-Type"] = "application/x-www-form-urlencoded"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=body, headers=all_headers, method=method)) as response:
            text = response.read().decode()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()[:300]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api", default="http://localhost:5188")
    parser.add_argument("--keycloak", default="http://localhost:8080")
    parser.add_argument("--fichas", type=int, default=10_000, help="fichas que el backoffice le acredita al jefe (0 = ninguna)")
    args = parser.parse_args()

    tokens = {}
    for name, password in DEV_USERS.items():
        status, body = request("POST", f"{args.keycloak}/realms/casino/protocol/openid-connect/token",
                               form={"grant_type": "password", "client_id": "casino-tests", "username": name, "password": password})
        if status != 200:
            raise SystemExit(f"No pude entrar como {name} ({status}). ¿Esta Keycloak levantado y el realm tiene a ese usuario?")
        tokens[name] = body["access_token"]

    # 1) Abrir las cuentas (el alta de jugador ocurre en el primer pedido).
    for name in ("jefe1", "cajero1", "jugador1", "jugador2"):
        status, _ = request("GET", f"{args.api}/me", tokens[name])
        print(f"alta de {name}: {status}")

    # 2) El arbol.
    admin = tokens["backoffice1"]
    placements = [(JEFE, "head_cashier", None), (CAJERO, "cashier", JEFE)] + [(p, "player", CAJERO) for p in JUGADORES]
    for user_id, level, parent in placements:
        status, body = request("PUT", f"{args.api}/backoffice/wallet/hierarchy/{user_id}", admin, {"level": level, "parentUserId": parent})
        if status == 404:
            raise SystemExit("La API no tiene /backoffice/wallet/hierarchy: reinicia la app (aspire run) con el codigo nuevo y volve a correr esto.")
        print(f"{user_id[-4:]} -> {level}: {status}" + ("" if status == 200 else f" {body}"))

    # 3) Fichas para el jefe (idempotente: la clave es fija).
    if args.fichas > 0:
        status, body = request("POST", f"{args.api}/backoffice/wallet/users/{JEFE}/credit", admin, {"amount": args.fichas},
                               headers={"Idempotency-Key": "dev-jerarquia-fichas-jefe1"})
        print(f"fichas para jefe1: {status}" + ("" if status == 200 else f" {body}"))

    print("Listo. Entra como jefe1 (/cajero) para cargarle a cajero1, y como cajero1 para cargarle a jugador1 y jugador2.")


if __name__ == "__main__":
    main()
