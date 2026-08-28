# Repository guidance

## Start here

- Read `docs/specification.md` for architecture, acceptance criteria, and roadmap ownership.
- Follow the nearest scoped instructions if later tasks add them.
- Keep changes within the ownership paths assigned by the roadmap or current task.

## Project conventions

- Target .NET 10. Nullable reference types, implicit usings, deterministic builds, warnings-as-errors, and SDK analyzers are configured centrally.
- Put package versions in `Directory.Packages.props`; project files contain versionless `PackageReference` items.
- Preserve dependency direction: Domain has no Vistara dependency; Application depends only on Domain; Contracts has no infrastructure dependency; infrastructure implements Application ports; API and Worker compose the system.
- Do not add infrastructure references to Domain or Application, backend references to frontend code, a generic repository abstraction, or a required mediator framework.
- Keep application ports focused and cancellation-aware. Keep API and Worker entry points thin; feature code belongs in its owned slice.
- Never commit credentials, signed URLs, raw private metadata, authorization headers, or local environment files.

## Verification

Run the narrowest relevant checks, then at minimum for shared bootstrap changes:

```bash
dotnet restore Vistara.slnx
dotnet build Vistara.slnx -c Release --no-restore
```
