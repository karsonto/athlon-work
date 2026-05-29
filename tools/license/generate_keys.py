#!/usr/bin/env python3
"""Generate RSA-2048 key pair for Athlon Agent license signing."""

from __future__ import annotations

import argparse
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives import serialization


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate RSA license signing keys.")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path(__file__).resolve().parent / "keys",
        help="Directory for private.pem and public.pem",
    )
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    private_path = args.output_dir / "private.pem"
    public_path = args.output_dir / "public.pem"

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    private_path.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    public_path.write_bytes(
        key.public_key().public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        )
    )

    print(f"Wrote {private_path}")
    print(f"Wrote {public_path}")
    print()
    print("Sync public.pem into src/Athlon.Agent.Infrastructure/Licensing/LicensePublicKey.cs")


if __name__ == "__main__":
    main()
