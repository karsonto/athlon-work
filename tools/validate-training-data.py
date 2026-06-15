#!/usr/bin/env python3
"""
Athlon Agent 训练数据验证脚本。

使用: python tools/validate-training-data.py [--dir ~/.athlon-agent/training-data]

检查:
1. JSON Lines 格式正确
2. messages 字段结构完整
3. role 值合法 (system/user/assistant/tool)
4. tool_calls 格式正确 (若有)
5. 输出统计摘要
"""

import argparse
import json
import os
import sys
from collections import Counter
from pathlib import Path


VALID_ROLES = {"system", "user", "assistant", "tool"}


def validate_file(filepath: str) -> dict:
    """验证单个 JSONL 文件，返回统计信息。"""
    stats = {
        "file": filepath,
        "total_lines": 0,
        "valid_lines": 0,
        "errors": [],
        "role_counts": Counter(),
        "has_corrections": 0,
        "has_dpo": 0,
        "avg_score": 0.0,
        "total_tokens_est": 0,
    }

    scores = []

    with open(filepath, "r", encoding="utf-8-sig") as f:
        for line_no, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue

            stats["total_lines"] += 1

            try:
                sample = json.loads(line)
            except json.JSONDecodeError as e:
                stats["errors"].append(f"Line {line_no}: JSON parse error - {e}")
                continue

            # 检测格式: SFT (messages) 或 DPO (prompt/chosen/rejected)
            if "messages" in sample:
                valid = _validate_sft_sample(sample, line_no, stats)
            elif "prompt" in sample and "chosen" in sample and "rejected" in sample:
                valid = _validate_dpo_sample(sample, line_no, stats)
            else:
                stats["errors"].append(
                    f"Line {line_no}: unknown format — expected 'messages' (SFT) or 'prompt'+'chosen'+'rejected' (DPO)"
                )
                continue

            if not valid:
                continue

            meta = sample.get("metadata")
            if not isinstance(meta, dict):
                stats["errors"].append(f"Line {line_no}: missing 'metadata' object")
                continue

            stats["valid_lines"] += 1

            if meta.get("hasCorrection"):
                stats["has_corrections"] += 1

            if "source" in meta and "dpo" in str(meta.get("source", "")).lower():
                stats["has_dpo"] += 1

            score = meta.get("score", 0.0)
            if isinstance(score, (int, float)):
                scores.append(score)

            tokens = meta.get("totalTokens", 0)
            if isinstance(tokens, (int, float)):
                stats["total_tokens_est"] += tokens

    if scores:
        stats["avg_score"] = sum(scores) / len(scores)

    return stats


def _validate_sft_sample(sample: dict, line_no: int, stats: dict) -> bool:
    """验证 SFT 格式 (messages 数组)。"""
    messages = sample.get("messages")
    if not isinstance(messages, list) or len(messages) == 0:
        stats["errors"].append(f"Line {line_no}: 'messages' must be a non-empty list")
        return False

    valid = True
    for j, msg in enumerate(messages):
        role = msg.get("role")
        if role not in VALID_ROLES:
            stats["errors"].append(f"Line {line_no}, msg[{j}]: invalid role '{role}'")
            valid = False

        stats["role_counts"][role] += 1

        if role == "assistant":
            tc = msg.get("tool_calls")
            if tc is not None:
                if not isinstance(tc, list):
                    stats["errors"].append(f"Line {line_no}, msg[{j}]: tool_calls must be a list")
                    valid = False
                else:
                    for k, tcall in enumerate(tc):
                        fn = tcall.get("function", {})
                        if not fn.get("name"):
                            stats["errors"].append(
                                f"Line {line_no}, msg[{j}], tool_call[{k}]: missing function.name"
                            )
                            valid = False

            if "reasoning" in msg:
                r = msg["reasoning"]
                if not isinstance(r, str) or len(r) == 0:
                    stats["errors"].append(
                        f"Line {line_no}, msg[{j}]: 'reasoning' must be a non-empty string"
                    )
                    valid = False

        if role == "tool":
            if not msg.get("tool_call_id"):
                stats["errors"].append(f"Line {line_no}, msg[{j}]: tool message missing tool_call_id")
                valid = False

    return valid


def _validate_dpo_sample(sample: dict, line_no: int, stats: dict) -> bool:
    """验证 DPO 格式 (prompt/chosen/rejected)。"""
    valid = True

    for section in ("prompt", "chosen", "rejected"):
        msgs = sample.get(section)
        if not isinstance(msgs, list) or len(msgs) == 0:
            stats["errors"].append(f"Line {line_no}: '{section}' must be a non-empty list")
            valid = False
            continue

        for j, msg in enumerate(msgs):
            role = msg.get("role")
            if role not in VALID_ROLES:
                stats["errors"].append(f"Line {line_no}, {section}[{j}]: invalid role '{role}'")
                valid = False

            stats["role_counts"][role] += 1

            if role == "assistant":
                tc = msg.get("tool_calls")
                if tc is not None:
                    if not isinstance(tc, list):
                        stats["errors"].append(f"Line {line_no}, {section}[{j}]: tool_calls must be a list")
                        valid = False
                    else:
                        for k, tcall in enumerate(tc):
                            fn = tcall.get("function", {})
                            if not fn.get("name"):
                                stats["errors"].append(
                                    f"Line {line_no}, {section}[{j}], tool_call[{k}]: missing function.name"
                                )
                                valid = False

            if role == "tool":
                if not msg.get("tool_call_id"):
                    stats["errors"].append(f"Line {line_no}, {section}[{j}]: tool message missing tool_call_id")
                    valid = False

    return valid


def main():
    parser = argparse.ArgumentParser(description="Validate Athlon Agent training data")
    parser.add_argument(
        "--dir",
        default=os.path.expanduser("~/.athlon-agent/training-data"),
        help="Training data directory (default: ~/.athlon-agent/training-data)",
    )
    args = parser.parse_args()

    data_dir = Path(args.dir)
    if not data_dir.exists():
        print(f"[ERROR] Directory not found: {data_dir}")
        sys.exit(1)

    jsonl_files = sorted(data_dir.glob("sft-traces-*.jsonl")) + sorted(data_dir.glob("dpo-preference-*.jsonl"))
    if not jsonl_files:
        print(f"[ERROR] No sft-traces-*.jsonl or dpo-preference-*.jsonl files found in {data_dir}")
        sys.exit(1)

    print(f"[INFO] Found {len(jsonl_files)} JSONL file(s) in {data_dir}")
    print()

    grand_total = {
        "total_lines": 0,
        "valid_lines": 0,
        "total_errors": 0,
        "has_corrections": 0,
        "has_dpo": 0,
        "total_tokens_est": 0,
    }
    all_scores = []

    for filepath in jsonl_files:
        stats = validate_file(str(filepath))
        grand_total["total_lines"] += stats["total_lines"]
        grand_total["valid_lines"] += stats["valid_lines"]
        grand_total["total_errors"] += len(stats["errors"])
        grand_total["has_corrections"] += stats["has_corrections"]
        grand_total["has_dpo"] += stats["has_dpo"]
        grand_total["total_tokens_est"] += stats["total_tokens_est"]

        status = "[OK]" if len(stats["errors"]) == 0 else "[WARN]"
        print(f"  {status} {filepath.name}")
        print(f"      Lines: {stats['total_lines']} total, {stats['valid_lines']} valid"
              f"  ({stats['total_lines'] - stats['valid_lines']} errors)")
        print(f"      Corrections: {stats['has_corrections']}"
              f"  |  DPO pairs: {stats['has_dpo']}"
              f"  |  Avg score: {stats['avg_score']:.2f}"
              f"  |  Est tokens: {stats['total_tokens_est']:,}")
        print(f"      Role distribution: {dict(stats['role_counts'])}")
        if stats["errors"]:
            for err in stats["errors"][:5]:
                print(f"      [WARN] {err}")
            if len(stats["errors"]) > 5:
                print(f"      ... and {len(stats['errors']) - 5} more error(s)")
        print()

        for sample_score in [stats.get("avg_score")]:
            if sample_score is not None:
                all_scores.append(sample_score)

    # Grand summary
    print("=" * 60)
    print("[SUMMARY]")
    print("=" * 60)
    print(f"  Files:            {len(jsonl_files)}")
    print(f"  Total lines:      {grand_total['total_lines']:,}")
    print(f"  Valid samples:    {grand_total['valid_lines']:,}")
    print(f"  Errors:           {grand_total['total_errors']}")
    print(f"  Has corrections:  {grand_total['has_corrections']:,}")
    print(f"  DPO preference:   {grand_total['has_dpo']:,}")
    print(f"  Est. total tokens:{grand_total['total_tokens_est']:,}")
    if grand_total['valid_lines'] > 0:
        print(f"  Avg tokens/sample:{grand_total['total_tokens_est'] // max(grand_total['valid_lines'], 1):,}")

    if grand_total["total_errors"] > 0:
        print()
        print("[WARN] Validation FAILED - fix errors above before training.")
        sys.exit(1)
    else:
        print()
        print("[OK] Validation PASSED - data is ready for HuggingFace datasets.load_dataset().")


if __name__ == "__main__":
    main()
