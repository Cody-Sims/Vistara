# Vistara Shadow architecture memory

`.shadow/` is Vistara's small, repository-owned decision graph. It is a review
aid, not a vendor format or a replacement for `docs/specification.md`, source,
tests, workflow configuration, or GitHub settings.

## Contents

- `schema.json` defines the JSON decision-record contract.
- `index.json` is the sorted inventory and relation vocabulary.
- `features.json` maps repository areas to an instruction owner, existing paths,
  and relevant decisions.
- `decisions/` contains one hand-authored JSON record per stable decision ID.

## Lifecycle

- `accepted`: explicitly approved architecture with specification evidence.
- `observed`: a constraint verified in current repository files.
- `proposed`: desired architecture awaiting maintainer approval.
- `superseded`: preserved history replaced by a reciprocal supersession link.

## Evidence

Every anchor and evidence item names an exact repository file and a unique
single-line locator. The validator rejects globs, missing paths, ambiguous
locators, broken relations, cycles, orphans, and index drift. Source remains
authoritative when a record becomes stale.

## Maintenance

Use `.github/skills/maintain-shadow/SKILL.md` for decision-graph work. Update the
record, sorted index, and feature links together, then run:

```bash
node eng/validate-shadow.mjs
node --test eng/tests/shadow-validator.test.mjs
```
