# Athlon Room Sandbox image

Runtime image used by local Docker sandbox and by `athlon-cloud-sandbox` sessions
(`default_image_tag: athlon-room-sandbox:2`).

Contents: Python 3.12, Temurin JDK 8, Node 24, Chromium.

## Local build

```bash
docker build -t athlon-room-sandbox:2 docker/athlon-room-sandbox
```

## CI / Docker Hub

GitHub Actions workflow: `.github/workflows/docker-room-sandbox.yml`

Publishes **`karsonto/athlon-room-sandbox`** tags:

- `2` (stable contract tag)
- `latest` (on default branch)
- semver / sha when triggered by a release tag

Repo secrets: `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`.
