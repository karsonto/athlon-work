#!/usr/bin/env bash
# Athlon Agent — 内网更新服务器同步脚本（单文件，改下面 CONFIG 即可）
#
# 用法:
#   ./sync-release.sh 2.0.1
#   ./sync-release.sh          # 交互输入版本号

set -euo pipefail

# =============================================================================
# CONFIG — 按你的环境修改这里
# =============================================================================
REPO="karsonto/athlon-work"
PROXY="http://127.0.0.1:7890"              # 不需要代理则设为 ""
DEPLOY_DIR="/var/www/athlon-agent"          # 内网更新目录（客户端 Update.BaseUrl 指向此路径）
# =============================================================================

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[sync]${NC} $*"; }
warn() { echo -e "${YELLOW}[warn]${NC} $*"; }
err()  { echo -e "${RED}[error]${NC} $*" >&2; }

usage() {
  cat <<EOF
用法: $(basename "$0") <version>

示例:
  $(basename "$0") 2.0.1
  $(basename "$0")              # 交互输入版本号

修改脚本顶部 CONFIG 区设置 REPO / PROXY / DEPLOY_DIR。
EOF
}

normalize_version() {
  local v="${1#v}"
  v="${v#V}"
  echo "$v"
}

prompt_version() {
  local v
  read -r -p "请输入版本号 (例如 2.0.1): " v
  if [[ -z "$v" ]]; then
    err "版本号不能为空。"
    exit 1
  fi
  normalize_version "$v"
}

curl_download() {
  local url="$1"
  local out="$2"
  local optional="${3:-0}"

  local args=(-fL --connect-timeout 30 --retry 3 --retry-delay 2 -o "$out")
  if [[ -n "$PROXY" ]]; then
    args+=(--proxy "$PROXY")
  fi

  if curl "${args[@]}" "$url"; then
    return 0
  fi

  if [[ "$optional" == "1" ]]; then
    warn "可选文件不存在，已跳过: $(basename "$out")"
    rm -f "$out"
    return 0
  fi

  err "下载失败: $url"
  return 1
}

download_release_file() {
  local base="$1"
  local name="$2"
  local optional="${3:-0}"
  curl_download "$base/$name" "$DEPLOY_DIR/$name" "$optional"
}

parse_assets_from_manifest() {
  local manifest="$1"
  if command -v python3 >/dev/null 2>&1; then
    python3 - "$manifest" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
for asset in data.get("Assets", []):
    print(asset.get("FileName", ""))
PY
    return
  fi

  if command -v jq >/dev/null 2>&1; then
    jq -r '.Assets[].FileName' "$manifest"
    return
  fi

  err "需要 python3 或 jq 来解析 releases.win.json"
  exit 1
}

verify_deploy_manifest() {
  local manifest="$DEPLOY_DIR/releases.win.json"
  if [[ ! -f "$manifest" ]]; then
    err "部署目录缺少 releases.win.json: $manifest"
    return 1
  fi

  local missing=0
  local name
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    if [[ ! -f "$DEPLOY_DIR/$name" ]]; then
      warn "清单引用但文件缺失: $name"
      missing=1
    fi
  done < <(parse_assets_from_manifest "$manifest")

  if [[ "$missing" -ne 0 ]]; then
    warn "releases.win.json 中部分包未找到。"
    warn "若是旧版本包，请先对该版本执行一次本脚本（DEPLOY_DIR 会保留历史 nupkg）。"
    return 1
  fi

  log "releases.win.json 中列出的包均已存在于 $DEPLOY_DIR"
  return 0
}

main() {
  local version="${1:-}"
  if [[ -z "$version" ]]; then
    version="$(prompt_version)"
  else
    version="$(normalize_version "$version")"
  fi

  local tag="v${version}"
  local base="https://github.com/${REPO}/releases/download/${tag}"

  if [[ -n "$PROXY" ]]; then
    export HTTP_PROXY="$PROXY"
    export HTTPS_PROXY="$PROXY"
    log "代理: $PROXY"
  fi

  log "仓库: $REPO"
  log "版本: $version ($tag)"
  log "目录: $DEPLOY_DIR"

  mkdir -p "$DEPLOY_DIR"

  log "[1/3] 下载 releases.win.json"
  download_release_file "$base" "releases.win.json" 0

  log "[2/3] 按清单下载 nupkg（full / delta）"
  local asset
  while IFS= read -r asset; do
    [[ -z "$asset" ]] && continue
    if [[ -f "$DEPLOY_DIR/$asset" ]]; then
      log "已存在，跳过: $asset"
      continue
    fi
    log "下载: $asset"
    download_release_file "$base" "$asset" 1 || true
  done < <(parse_assets_from_manifest "$DEPLOY_DIR/releases.win.json")

  log "[3/3] 下载安装包与元数据"
  for extra in \
    "AthlonAgent-win-Setup.exe" \
    "AthlonAgent-win-Portable.zip" \
    "assets.win.json" \
    "RELEASES"; do
    log "下载: $extra"
    download_release_file "$base" "$extra" 1 || true
  done

  log "目录内容:"
  ls -lah "$DEPLOY_DIR"

  log "校验更新清单..."
  if verify_deploy_manifest; then
    log "版本 $version 同步完成。"
  else
    warn "同步完成但有告警，请检查上方缺失文件。"
    exit 2
  fi
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

main "$@"
