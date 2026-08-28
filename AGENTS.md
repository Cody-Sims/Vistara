# Vistara agent navigation

## Start here

1. Read `docs/specification.md`; it is the product, architecture, acceptance, and roadmap authority.
2. Load the scoped instruction matching the paths you will change.
3. Consult `.shadow/index.json` when a change affects a durable architecture decision.
4. Stay inside the ownership paths assigned by the current roadmap task.

## Scoped guidance

| Scope | Instruction |
|---|---|
| .NET projects and source | `.github/instructions/dotnet.instructions.md` |
| React application | `.github/instructions/web.instructions.md` |
| Test code and fixtures | `.github/instructions/testing.instructions.md` |
| Containers and deployment | `.github/instructions/deployment.instructions.md` |
| GitHub Actions | `.github/instructions/workflows.instructions.md` |
| Agent customization and Shadow records | `.github/instructions/agent-tooling.instructions.md` |

## Architecture memory

`.shadow/README.md` explains the repository decision graph. Activate
`.github/skills/maintain-shadow/SKILL.md` only for work that inspects or changes
that graph.

## Tooling checks

Run the narrowest relevant product checks. For agent or architecture-memory
changes, run:

```bash
node eng/validate-shadow.mjs
node eng/validate-agent-workflows.mjs
node --test eng/tests/*.test.mjs
```
