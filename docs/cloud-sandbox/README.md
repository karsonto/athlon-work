# Cloud Sandbox contract (desktop)

Proto (source of truth): copied from `athlon-cloud-sandbox/proto/athlon/cloud_sandbox/v1/sandbox.proto`
into [`src/Athlon.Agent.Infrastructure/CloudSandbox/Proto/sandbox.proto`](../../src/Athlon.Agent.Infrastructure/CloudSandbox/Proto/sandbox.proto).

Control plane lives in the separate repo `athlon-cloud-sandbox` and speaks **gRPC** on port **9090** (h2c).
Every request/response is wrapped in `SecureEnvelope` and sealed with **AES-256-GCM** using a shared key.

## Desktop settings

| Key | Meaning |
|-----|---------|
| `CloudSandbox:BaseUrl` | gRPC base URL, e.g. `http://cloud-sandbox.corp.local:9090` |
| `CloudSandbox:ApiKey` | Caller identity (`x-api-key` / Bearer metadata) |
| `CloudSandbox:SharedKey` | Base64 32-byte AES-256 key (required) |
| `PreferredRegion` / `NetworkPolicy` / `PreferIncrementalSync` | unchanged |

`IsConfigured` requires BaseUrl + ApiKey + SharedKey.

## Encryption frame

```
version(1)=0x01 | nonce(12) | ciphertext | tag(16)
AAD = "<full_rpc_method>|<session_id>"
```

Session-scoped RPCs also send `x-session-id` metadata matching the AAD session id.
`Status` / `CreateSession` use an empty session id in AAD.

## Sync (push-only)

Same semantics as before, over gRPC:

1. Desktop builds desired manifest `{ path, size, mtimeUnixMs, sha256 }[]`.
2. `SyncManifest` → `need_upload` paths the desktop must send.
3. `SyncDelta` client-stream: header + blobs (full file per path).
4. `SyncFull` client-stream: header + tar.gz chunks (fallback).
5. `PullWorkspace` server-stream: tar.gz chunks after a turn.

This sync manifest is unrelated to Athlon Skill package manifests.
