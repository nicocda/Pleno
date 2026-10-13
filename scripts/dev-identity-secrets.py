#!/usr/bin/env python3
"""Genera las claves de desarrollo del servicio de identidad y las guarda con user-secrets (nunca en el repositorio).

Uso (una sola vez por maquina; si ya existen no se pisan, porque perder la clave del email deja ilegibles los emails ya guardados):

    python scripts/dev-identity-secrets.py

Guarda en src/Casino.AppHost:
  Parameters:identity-email-key       32 bytes en base64: cifra los emails (AES-256-GCM)
  Parameters:identity-email-hmac-key  32 bytes en base64: hash con clave para buscar emails sin descifrar (distinta a la anterior)
"""
import base64
import os
import subprocess
import sys

PROJECT = "src/Casino.AppHost"
KEYS = ["Parameters:identity-email-key", "Parameters:identity-email-hmac-key"]


def run(*args: str) -> str:
    result = subprocess.run(["dotnet", "user-secrets", *args, "--project", PROJECT], capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(result.stderr.strip() or result.stdout.strip())
    return result.stdout


def main() -> None:
    existing = run("list")
    for key in KEYS:
        if f"{key} =" in existing:
            print(f"{key}: ya existe, no se toca")
            continue
        run("set", key, base64.b64encode(os.urandom(32)).decode())
        print(f"{key}: generada")


if __name__ == "__main__":
    main()
