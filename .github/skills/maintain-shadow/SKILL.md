---
name: maintain-shadow
description: "Maintains Vistara .shadow decisions, evidence anchors, relations, indexes, and feature ownership. Use when inspecting architecture memory, recording an approved decision, proposing a boundary, or checking Shadow drift."
license: MIT
metadata:
  version: "1.0.0"
  author: "Cody-Sims"
  tier: "core"
---

# Maintain Vistara Shadow

Maintain the repository-owned architecture decision graph without replacing its
authoritative specification, source, or tests.

## Workflow

1. Read `AGENTS.md`, `.shadow/README.md`, `.shadow/index.json`, and
   `.shadow/features.json`.
2. Select only decisions connected to the affected feature and verify every
   referenced locator against the current file.
3. Classify reconstructed implementation as `observed`, approved direction as
   `accepted`, and unapproved direction as `proposed`.
4. Add a new accepted record to replace history; link it with reciprocal
   `supersedes` and `superseded_by` relations rather than rewriting the old
   decision.
5. Keep IDs stable, records and feature links sorted, and every decision mapped
   to at least one owning feature.
6. Run `node eng/validate-shadow.mjs` and the validator test suite.
7. Report changed records, lifecycle states, evidence, drift, and proposals that
   still need maintainer approval.

## Boundaries

- Do not infer historical intent or promote a proposal without explicit approval.
- Do not use generated views as evidence.
- Do not copy product rules into this skill; follow their scoped instruction owner.
