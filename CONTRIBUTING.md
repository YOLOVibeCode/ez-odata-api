# Contributing to ez-odata-api

Thanks for your interest in improving ez-odata-api. This project is built test-first;
contributions are expected to keep the suite green and follow a couple of load-bearing
conventions.

## Getting set up

```bash
dotnet restore EzOdata.slnx
dotnet build EzOdata.slnx -c Release
dotnet test                       # unit + integration (needs Docker) + conformance + security
cd ui && npm install && npm run build
```

Docker is required for the integration and conformance suites — they spin up real
PostgreSQL / MySQL / SQL Server instances via Testcontainers.

## Conventions that matter

- **Test-first (TDD).** Production code in `Core`, `OData`, `Rest`, `Mcp`, and the
  connectors is written against a failing test first. New protocol behavior should land
  as a conformance case in `tests/EzOdata.ConformanceTests/cases/*.yaml` where possible.
- **Interface Segregation (ISP).** Abstractions are small role interfaces (1–4 members).
  An architecture test enforces this and the layering rules — see
  `tests/EzOdata.UnitTests/Architecture/LayeringTests.cs`. Don't add a member that an
  implementation would satisfy with `throw new NotSupportedException`.
- **The engine stays `netstandard2.0`.** `Core`, the connectors, `OData`, `Rest`, and
  `Docs` must run on .NET Framework 4.8. No APIs outside that surface (a test guards the
  TFM). Platform-specific code belongs in `Host`, `Data`, `Admin`, `Mcp`, or the adapters.
- **SQL is always parameterized.** Client-supplied values never reach SQL text; the
  security suite drives an injection corpus through every surface and must stay green.

## Pull requests

1. Branch from `main`.
2. Keep `dotnet test` green (CI runs the full matrix on every push/PR).
3. Describe the "why" in the PR; reference the relevant `spec/` section if it changes
   documented behavior.

## Making a release

Both the NuGet packages and the npm package (`@noctusoft/ezodata-express`) are
published from a single `v*` tag. The tag version drives both publishes
automatically — you do **not** need to manually bump `Directory.Build.props`
or `js/package.json` before tagging (the CI workflow injects the version at
pack/publish time via the tag name).

### Steps

1. **Optionally pre-sync the version in source** (cosmetic, not required for
   the publish to work):
   - `Directory.Build.props` → `<Version>X.Y.Z</Version>`
   - `js/package.json` → `"version": "X.Y.Z"`

2. **Create and push the tag:**

   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```

3. **CI takes over.** The `v1.2.3` tag triggers:
   - `dotnet-check` — full .NET build + unit, integration, and conformance tests
   - `js-check` — typecheck, lint, dependency-cruise, and Vitest tests
   - `publish-nuget` (after `dotnet-check` passes) — packs with `-p:Version=1.2.3`
     and pushes to GitHub Packages (idempotent via `--skip-duplicate`)
   - `publish-npm` (after `js-check` passes) — sets `package.json` version to
     `1.2.3`, builds, and publishes to npm using `secrets.NPM_TOKEN`

### JS-only patch releases

If you need to ship a JS fix without touching the .NET packages, use a
`js-v*` tag instead:

```bash
git tag js-v1.2.4
git push origin js-v1.2.4
```

Only `js-check` and `publish-npm` will run; the .NET jobs are skipped.

## License

By contributing, you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE).
