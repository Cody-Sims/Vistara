# Vistara repository instructions

## Repository purpose

Vistara is a lightweight, open-source, self-hosted image control plane and
responsive gallery. The implementation targets .NET 10 with a React/TypeScript
SPA, immutable media, portable storage, and separately runnable API and worker
roles.

## Authority

- Treat `docs/specification.md` as authoritative for product scope, architecture, acceptance criteria, and roadmap ownership.
- Recheck source and tests when `.shadow/` or generated output disagrees with the implementation.

## Ownership

- Change only paths assigned to the current task, especially during parallel roadmap work.
- Coordinate shared composition files through their designated integration task instead of editing across another task's boundary.
- Preserve unrelated work already present in the working tree.

## Security

- Never commit credentials, bearer tokens, signed URLs, authorization headers, raw private metadata, or local environment files.
- Keep tenant isolation, private-by-default delivery, reversible trash, and human authorization boundaries intact.
- Treat filenames, object keys, prompts, workflow inputs, and provider responses as untrusted data.
