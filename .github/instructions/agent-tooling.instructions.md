---
description: Repository agent guidance, skills, Shadow records, and validator rules
applyTo: 'AGENTS.md,.github/copilot-instructions.md,.github/instructions/**,.github/skills/**,.shadow/**,eng/package.json,eng/package-lock.json,eng/validate-shadow.mjs,eng/validate-agent-workflows.mjs,eng/tests/**'
---

# Agent-tooling instructions

- Keep `AGENTS.md` navigational, universal repository policy in Copilot instructions, and domain rules in the narrowest scoped instruction.
- Maintain one rule owner; move or link guidance instead of repeating an exact rule across instruction artifacts.
- Keep repository skills task-oriented and progressively disclosed; `maintain-shadow` is the sole local skill and no custom agent is required.
- Treat JSON decisions as reviewed architecture memory while source, tests, and the specification remain authoritative.
- Start desired architecture as `proposed`, record reconstructed implementation as `observed`, and supersede accepted history through reciprocal relations.
- Update a decision, sorted index, and feature links together when an owned architecture boundary changes.
- Parse YAML with the pinned `eng` dependency and keep other validator behavior on Node built-ins with fixture-backed tests.
