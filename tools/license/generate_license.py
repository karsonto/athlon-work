#!/usr/bin/env python3
"""Issue a signed Athlon Agent license bound to a Windows AD account."""

from __future__ import annotations

import argparse
import base64
import json
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding


PRODUCT = "athlon-agent"
VERSION = 1


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate Athlon Agent license (.lic)")
    parser.add_argument(
        "--account",
        required=True,
        help='AD account: DOMAIN\\user or user@domain.com',
    )
    parser.add_argument(
        "--sam",
        help="Optional explicit Sam account name (DOMAIN\\user)",
    )
    parser.add_argument(
        "--upn",
        help="Optional explicit UPN (user@domain.com)",
    )
    parser.add_argument(
        "--days",
        type=int,
        default=30,
        help="Validity in days from now (default: 30). Ignored when --expires is set.",
    )
    parser.add_argument(
        "--expires",
        help="Expiry as ISO-8601 UTC, e.g. 2026-12-31T23:59:59Z",
    )
    parser.add_argument(
        "--license-id",
        help="License UUID (default: random)",
    )
    parser.add_argument(
        "--private-key",
        type=Path,
        default=Path(__file__).resolve().parent / "keys" / "private.pem",
        help="RSA private key PEM",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("license.lic"),
        help="Output .lic file path",
    )
    return parser.parse_args()


def classify_account(account: str) -> tuple[str, str | None, str | None]:
    account = account.strip()
    if "@" in account:
        return account, None, account
    if "\\" in account:
        return account, account, None
    return account, account, None


def parse_expires(args: argparse.Namespace, issued_at: datetime) -> datetime:
    if args.expires:
        text = args.expires.replace("Z", "+00:00")
        return datetime.fromisoformat(text).astimezone(timezone.utc)
    return issued_at + timedelta(days=args.days)


def sign_envelope(private_key_path: Path, payload: dict) -> dict:
    payload_json = json.dumps(payload, separators=(",", ":"), ensure_ascii=False)
    payload_b64 = base64.b64encode(payload_json.encode("utf-8")).decode("ascii")

    private_key = serialization.load_pem_private_key(
        private_key_path.read_bytes(),
        password=None,
    )
    signature = private_key.sign(
        payload_b64.encode("utf-8"),
        padding.PKCS1v15(),
        hashes.SHA256(),
    )
    return {
        "payloadB64": payload_b64,
        "signatureB64": base64.b64encode(signature).decode("ascii"),
    }


def main() -> None:
    args = parse_args()
    if not args.private_key.is_file():
        raise SystemExit(f"Private key not found: {args.private_key}")

    issued_at = datetime.now(timezone.utc)
    expires_at = parse_expires(args, issued_at)

    ad_account, inferred_sam, inferred_upn = classify_account(args.account)
    ad_account_sam = args.sam or inferred_sam
    ad_account_upn = args.upn or inferred_upn

    payload = {
        "version": VERSION,
        "product": PRODUCT,
        "licenseId": args.license_id or str(uuid.uuid4()),
        "issuedAt": issued_at.isoformat().replace("+00:00", "Z"),
        "expiresAt": expires_at.isoformat().replace("+00:00", "Z"),
        "adAccount": ad_account,
    }
    if ad_account_sam:
        payload["adAccountSam"] = ad_account_sam
    if ad_account_upn:
        payload["adAccountUpn"] = ad_account_upn

    envelope = sign_envelope(args.private_key, payload)
    args.output.write_text(
        json.dumps(envelope, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(f"Wrote {args.output.resolve()}")
    print(f"  account: {ad_account}")
    print(f"  expires: {payload['expiresAt']}")


if __name__ == "__main__":
    main()
