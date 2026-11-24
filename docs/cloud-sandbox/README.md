# Cloud Sandbox contract (desktop)

OpenAPI: [v1.yaml](./v1.yaml)

Control plane lives in the separate repo `athlon-cloud-sandbox`.
Desktop settings: `CloudSandbox:BaseUrl`, `CloudSandbox:ApiKey`, optional `PreferredRegion` / `NetworkPolicy` / `PreferIncrementalSync`.

Auth and metering are handled by the enterprise API gateway. This desktop client does not implement quota ledgers.
