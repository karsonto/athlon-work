#!/usr/bin/env python3
"""Write skill manifests with the same canonical SHA-256 bytes as the platform and the agent."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path


def escape_json(value: str) -> str:
    out = ['"']
    for ch in value:
        code = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\b":
            out.append("\\b")
        elif ch == "\f":
            out.append("\\f")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        elif code < 0x20:
            out.append("\\u%04x" % code)
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def canonical(skill_id: str, commit_seq: int, files: list[tuple[str, int, str]]) -> str:
    if not skill_id or not skill_id.strip():
        raise ValueError("skillId is required")
    if commit_seq < 1:
        raise ValueError("commitSeq must be >= 1")
    ordered = sorted(files, key=lambda item: item[0])
    parts = ['{"commitSeq":', str(commit_seq), ',"files":[']
    for index, (path, size, sha) in enumerate(ordered):
        validate_entry(path, size, sha)
        if index:
            parts.append(",")
        parts.append('{"path":')
        parts.append(escape_json(path))
        parts.append(',"sha256":')
        parts.append(escape_json(sha))
        parts.append(',"size":')
        parts.append(str(size))
        parts.append("}")
    parts.append('],"skillId":')
    parts.append(escape_json(skill_id.strip()))
    parts.append("}")
    return "".join(parts)


def validate_entry(path: str, size: int, sha: str) -> None:
    validate_relative_path(path)
    if size < 0 or len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
        raise ValueError("manifest entry is invalid")


def validate_relative_path(path: str) -> None:
    if not path or path[0] == "/" or "\\" in path or ":" in path:
        raise ValueError("illegal skill path")
    for part in path.split("/"):
        if part in ("", ".", ".."):
            raise ValueError("illegal skill path")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def list_files(skill_dir: Path) -> list[tuple[str, int, str]]:
    root = skill_dir.resolve()
    files: list[tuple[str, int, str]] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.resolve().relative_to(root).as_posix()
        validate_relative_path(relative)
        data_size = path.stat().st_size
        files.append((relative, data_size, sha256_file(path)))
    files.sort(key=lambda item: item[0])
    return files


def read_skill_id(skill_dir: Path) -> str:
    skill_md = skill_dir / "SKILL.md"
    if not skill_md.is_file():
        raise ValueError("SKILL.md is required")
    text = skill_md.read_text(encoding="utf-8")
    if text.startswith("---"):
        end = text.find("\n---", 3)
        front = text[:end] if end >= 0 else ""
        for line in front.splitlines():
            stripped = line.strip()
            if stripped.startswith("name:"):
                value = stripped[5:].strip().strip("\"'")
                if value:
                    return value
    return skill_dir.name


def safe_file_name(skill_id: str) -> str:
    if not skill_id or not skill_id.strip():
        raise ValueError("skillId is required")
    chars = []
    for ch in skill_id.strip():
        chars.append(ch if ch.isalnum() or ch in "._-" else "_")
    name = "".join(chars).strip(" .")
    if not name or name in (".", ".."):
        raise ValueError("invalid skillId")
    return name


def read_commit_seq(manifest_path: Path) -> int:
    if not manifest_path.is_file():
        return 0
    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    return int(payload.get("commitSeq") or 0)


def update_anchor(anchor_path: Path, skill_id: str, commit_seq: int) -> None:
    current: dict[str, int] = {}
    if anchor_path.is_file():
        loaded = json.loads(anchor_path.read_text(encoding="utf-8"))
        if isinstance(loaded, dict):
            for key, value in loaded.items():
                current[str(key)] = int(value)
    previous = current.get(skill_id, 0)
    if commit_seq < previous:
        raise ValueError("commitSeq is older than the anchor")
    current[skill_id] = commit_seq
    anchor_path.parent.mkdir(parents=True, exist_ok=True)
    anchor_path.write_text(json.dumps(current, ensure_ascii=False, sort_keys=True, separators=(",", ":")), encoding="utf-8")


def sign_directory(skill_dir: Path, manifest_dir: Path, commit_seq: int | None) -> Path:
    skill_id = read_skill_id(skill_dir)
    target = manifest_dir / (safe_file_name(skill_id) + ".json")
    existing = read_commit_seq(target)
    seq = existing + 1 if commit_seq is None else commit_seq
    if existing and seq < existing:
        raise ValueError("commitSeq must not be lower than the existing manifest")
    if seq < 1:
        seq = 1
    body = canonical(skill_id, seq, list_files(skill_dir))
    manifest_dir.mkdir(parents=True, exist_ok=True)
    target.write_text(body, encoding="utf-8", newline="\n")
    update_anchor(manifest_dir.parent / "skill-anchors.json", skill_id, seq)
    return target


def self_test() -> None:
    basic = canonical("demo", 1, [("SKILL.md", 4, "a" * 64)])
    expected = '{"commitSeq":1,"files":[{"path":"SKILL.md","sha256":"' + ("a" * 64) + '","size":4}],"skillId":"demo"}'
    if basic != expected:
        raise SystemExit("canonical mismatch: " + basic)
    unicode_body = canonical("技能", 2, [])
    expected_unicode = '{"commitSeq":2,"files":[],"skillId":"技能"}'
    if unicode_body != expected_unicode:
        raise SystemExit("unicode canonical mismatch: " + unicode_body)
    ordered = canonical("demo", 1, [("b.txt", 1, "b" * 64), ("a.txt", 1, "c" * 64)])
    if '"path":"a.txt"' not in ordered or ordered.index("a.txt") > ordered.index("b.txt"):
        raise SystemExit("path sort mismatch")


def main() -> None:
    parser = argparse.ArgumentParser(description="Sign a skill directory into the agent manifest store.")
    parser.add_argument("--skill-dir", type=Path)
    parser.add_argument("--skills-root", type=Path)
    parser.add_argument("--manifest-dir", type=Path, default=Path.home() / ".athlon-agent" / "config" / "skill-manifests")
    parser.add_argument("--commit-seq", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        print("ok")
        return
    targets: list[Path] = []
    if args.skill_dir:
        targets.append(args.skill_dir)
    if args.skills_root:
        targets.extend(path for path in sorted(args.skills_root.iterdir()) if (path / "SKILL.md").is_file())
    if not targets:
        parser.error("pass --skill-dir or --skills-root")
    for skill_dir in targets:
        written = sign_directory(skill_dir, args.manifest_dir, args.commit_seq)
        print(written)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
