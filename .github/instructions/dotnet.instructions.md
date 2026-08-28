---
description: .NET architecture, dependency, package, and implementation rules
applyTo: 'Directory.Build.props,Directory.Packages.props,Vistara.slnx,src/**/*.cs,src/**/*.csproj,tests/**/*.cs,tests/**/*.csproj'
---

# .NET instructions

- Target .NET 10 with nullable reference types, implicit usings, deterministic builds, warnings as errors, and configured SDK analyzers.
- Place package versions in `Directory.Packages.props`; keep project `PackageReference` entries versionless.
- Preserve the dependency graph enforced by `tests/Vistara.ArchitectureTests`: Domain has no Vistara dependency, Application depends only on Domain, Contracts avoids infrastructure, adapters implement Application ports, and API/Worker are terminal composition roots.
- Keep application ports focused, provider-neutral, and cancellation-aware; do not introduce a generic repository or mandatory mediator abstraction.
- Put feature behavior in its owned slice and keep executable entry points limited to composition and hosting.
- Generate UUIDv7 identifiers in application code, use UTC timestamps, and keep tenant-owned data explicitly tenant-scoped.
