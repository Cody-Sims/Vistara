# Vistara

Vistara is an open-source, self-hosted image control plane and responsive gallery. The repository currently contains the .NET 10 solution bootstrap defined by BOOT-01; product features, the React application, deployment assets, and CI arrive in later roadmap tasks.

## Prerequisites

- .NET 10 SDK (the feature band is pinned in `global.json`)

## Build

```bash
dotnet restore Vistara.slnx
dotnet build Vistara.slnx -c Release --no-restore
```

## Layout

- `src/`: domain, application, contracts, infrastructure adapters, API, and worker projects.
- `tests/`: .NET unit, architecture, contract, integration, conformance, imaging, migration, and performance test projects.
- `docs/specification.md`: approved product specification and ordered implementation roadmap.

The React project (`src/Vistara.Web`), browser E2E suite (`tests/Vistara.E2E`), deployment definitions, and CI workflows are intentionally reserved for their roadmap owners.

Licensed under the [Apache License 2.0](LICENSE).
