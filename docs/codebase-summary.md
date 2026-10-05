# Codebase summary

| Path | Responsibility |
|---|---|
| `src/Omts.Api/` | ASP.NET Core hosting, operational routes, configuration and telemetry. |
| `tests/` | Integration tests of real hosting contracts and startup validation. |
| `Omts.Backend.slnx` | Solution used by root dotnet commands. |
| `global.json` | Pinned SDK. |
| `Directory.Build.props` | Shared build and analyzer rules. |
| `Directory.Packages.props` | Central package versions. |
| `nuget.config` | Approved package/audit source configuration. |
| `Dockerfile` | Container build and non-root runtime packaging. |
| `.github/workflows/` | Source verification, PR convention check and image publication. |
| `.github/` | Ownership, dependency updates, issue/PR templates and intended branch policy. |
| `docs/` | Architecture, code, operations and governance guidance. |

No business entities, persistence or authentication provider are defined yet. Introduce these from accepted domain requirements and document their contracts.
