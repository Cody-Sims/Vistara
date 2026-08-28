# Vistara

Vistara is an open-source, self-hosted image control plane and responsive gallery. The repository currently includes the .NET 10 foundation, core domain models, the React application shell, container builds, and repository automation. Storage, persistence, API features, and worker processing remain roadmap work.

## Prerequisites

- .NET 10 SDK (the feature band is pinned in `global.json`)
- Node.js 24 and npm
- Docker for container validation

## Validate

```bash
dotnet restore Vistara.slnx
dotnet build Vistara.slnx -c Release --no-restore
dotnet test Vistara.slnx -c Release --no-build --no-restore
npm --prefix src/Vistara.Web ci
npm --prefix src/Vistara.Web run check
npm --prefix eng ci
node eng/validate-shadow.mjs
node eng/validate-agent-workflows.mjs
node --test eng/tests/*.test.mjs
```

`npm --prefix src/Vistara.Web run build:pages` creates a GitHub Pages artifact under `src/Vistara.Web/dist`. That artifact is a static preview only; it has no API, authentication, uploads, persistence, or worker processing.

## Layout

- `src/`: domain, application, contracts, infrastructure adapters, API, and worker projects.
- `tests/`: .NET unit, architecture, contract, integration, conformance, imaging, migration, and performance test projects.
- `eng/`: repository and Shadow validators with isolated Node dependencies.
- `.shadow/`: evidence-linked architecture decisions.
- `.github/workflows/`: CI, security, Pages preview, and release-image automation.
- `docs/specification.md`: approved product specification and ordered implementation roadmap.

Licensed under the [Apache License 2.0](LICENSE).
