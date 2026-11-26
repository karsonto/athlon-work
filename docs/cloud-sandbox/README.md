# Cloud Sandbox contract (desktop)

OpenAPI: [v1.yaml](./v1.yaml)

Control plane lives in the separate repo `athlon-cloud-sandbox`.
Desktop settings: `CloudSandbox:BaseUrl`, `CloudSandbox:ApiKey`, optional `PreferredRegion` / `NetworkPolicy` / `PreferIncrementalSync`.

Auth and metering are handled by the enterprise API gateway. This desktop client does not implement quota ledgers.

## Sync manifest (push-only)

This is **not** an Athlon Skill package manifest. It describes files the desktop wants on the control-plane staging area.

1. Desktop builds a **desired** manifest: `{ path, size, mtimeUnixMs, sha256 }[]`.
2. `POST /v1/sessions/{id}/sync/manifest` — server compares desired to **current staging**.
3. Response `need` / `needUpload` = paths the **desktop must upload** (sha missing or changed). `mtimeUnixMs` is ignored for decisions.
4. `POST /v1/sessions/{id}/sync/delta` — full desired manifest + blobs for `needUpload`; staging extras are deleted.
5. Full `POST /v1/sessions/{id}/sync` (tar.gz) **replaces** staging the same way (clear + extract).

When skills are filtered by selected names, the filtered tree is the full desired set for `kind=skills` (unselected skills are removed from staging).
