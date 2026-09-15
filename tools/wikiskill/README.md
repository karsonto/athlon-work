# WikiSkill for Athlon Agent

Offline skill evolution inspired by [WikiSkill (arXiv:2608.27454)](https://arxiv.org/abs/2608.27454).

Scans Athlon local data under `~/.athlon-agent/`, consolidates experience into a persistent **Wiki**, then proposes **Skill** create/patch updates for review.

## Three layers (paper-aligned)

| Layer | Location | Role |
|-------|----------|------|
| **Raw** | `~/.athlon-agent/wikiskill-workspace/raw/traces/` | Immutable traces from sessions / training-data / behavior |
| **Wiki** | `.../wiki/{index.md,logs.md,skill-impact.md,patterns/}` | Persistent patterns; **never rolled back** |
| **Skills** | `~/.athlon-agent/skills/` | Executable `SKILL.md` modules (applied only after review) |

Proposals land in `wikiskill-workspace/proposals/` first. Applying a proposal updates `skills/` and appends an accept/reject entry to `wiki/skill-impact.md`.

## Quick start

```bash
# Sanity check with synthetic data (no Athlon history required)
python3 tools/wikiskill/evolve.py selftest

# Full loop: scan → wiki → propose (does NOT auto-install skills)
python3 tools/wikiskill/evolve.py evolve

# Inspect
python3 tools/wikiskill/evolve.py status

# Review proposals
ls ~/.athlon-agent/wikiskill-workspace/proposals/

# Install one proposal into Athlon skills/
python3 tools/wikiskill/evolve.py apply --proposal <proposal_id>

# Dry-run apply
python3 tools/wikiskill/evolve.py apply --proposal <proposal_id> --dry-run

# Reject (record only, keep wiki)
python3 tools/wikiskill/evolve.py apply --proposal <proposal_id> --reject
```

### Step by step

```bash
python3 tools/wikiskill/evolve.py scan --max-sessions 80 --days 30
python3 tools/wikiskill/evolve.py maintain-wiki
python3 tools/wikiskill/evolve.py propose --max-proposals 3
```

### LLM-assisted mode (optional)

Default mode is **rule-based** (no network). For deeper consolidation:

```bash
export ATHLON_ENDPOINT=https://api.openai.com/v1   # or your OpenAI-compatible base
export ATHLON_API_KEY=sk-...
export ATHLON_MODEL=gpt-4o-mini

python3 tools/wikiskill/evolve.py evolve --llm
```

Also accepts `OPENAI_BASE_URL` / `OPENAI_API_KEY` / `OPENAI_MODEL`.

> Athlon desktop stores API keys with DPAPI on Windows; this script does **not** read those secrets. Use env vars.

## What it reads from Athlon

| Source | Path | Signal |
|--------|------|--------|
| Sessions | `sessions/*/session.json` (+ `conversation.jsonl`) | Tool calls, `Error:` results, user corrections |
| Training data | `training-data/sft-traces-*.jsonl` | Correction / overflow trajectories |
| Behavior | `behavior/pending.jsonl` | Aggregated tool failure events |
| Skills | `skills/*/SKILL.md` | Existing skills to patch vs create |

## Design notes (from the paper)

1. **Inference vs Wiki**: Athlon runtime should keep loading **Skills** only (`load_skill_through_path`). Do not inject the Wiki into normal chat — the paper’s ablation shows training-time wiki access hurts skill quality.
2. **Gating**: This tool uses **human review** as the gate (safer for a local desktop agent). Auto-apply is available via `evolve --apply-first` but not recommended until you trust proposals.
3. **Wiki never rolls back**: Rejected proposals still append to `skill-impact.md` so later proposers avoid repeating failed edits.
4. **Atomic proposals**: Prefer one create/patch at a time; review each proposal markdown before apply.

## Output skill example

Rule-based evolution typically creates:

- `athlon-wikiskill-hardening` — bundled failure/correction lessons
- `fix-<tool>` — focused recovery skill for the noisiest failing tool

After apply, restart Athlon or reload skills so the catalog picks up new folders under `~/.athlon-agent/skills/`.

## Dependencies

Python 3.10+ standard library only (`urllib` for `--llm`). No `pip install` required for rule-based mode.

## Limitations

- No automated validation-set scoring (Athlon is interactive; use human gate or your own eval harness).
- WebSocket / Computer Use / Browser DevTools traces are not specialized yet.
- Wiki has no auto-prune; delete stale `patterns/*.md` manually if it grows large.
- LLM mode quality depends on your model and prompt budget.
