# Platform report

Status: DONE

Implemented .NET 10 ASP.NET Core API and tests with SDK 10.0.401 pinned, centrally pinned NuGet packages, committed lock files, deterministic builds, nullable checks, analyzers, warnings as errors, and explicit NuGet.org audit source.

## Verified

Using an isolated installation of SDK 10.0.401:

- `restore --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low`: passed.
- `build --configuration Release --no-restore`: passed, zero warnings/errors.
- `test --configuration Release --no-build`: passed, 28 tests, zero failures/skips.
- `format --verify-no-changes --no-restore`: passed.

Tests cover health JSON, no exposed readiness dependency details, lifecycle readiness, explicit host allowlists and rejection, startup failure for invalid limits/hosts, no CORS permission headers, generic 404/500 for JSON/HTML/plain Accept headers, 413 limits, and cancellation-based 504 timeout. Test-only fault injection resides entirely in the test assembly.

## Configuration and limits

- Production and Staging require explicit `AllowedHosts` names/IPs, separated by semicolons. Wildcards, URLs, ports, and catch-all bind addresses fail startup. Include probe hosts if used.
- Development launch profile uses `http://localhost:8080`; local allowlist includes localhost and loopback IPs.
- `Hosting__MaxRequestBodyBytes`: default 1048576, range 1..104857600; known Content-Length rejected before dispatch, Kestrel limits streamed body reads.
- `Hosting__RequestTimeoutSeconds`: default 30, range 1..300; endpoints must honor cancellation. Middleware cannot forcibly stop uncooperative work or rewrite a started response.
- `OTEL_EXPORTER_OTLP_ENDPOINT`: nonempty enables OTLP metrics/traces. Standard exporter protocol/headers environment variables remain supported. No exporter without this endpoint. No external collector tested.
- Health exposes only status. Liveness checks process responsiveness; readiness checks application lifecycle plus checks tagged `ready`.
- Generic application errors use RFC 9457 `application/problem+json`, `about:blank`, status/title/traceId only. Framework host filtering returns an empty 400 before the application pipeline; transport-level rejection may also occur before it.
- TLS termination and network exposure belong to a trusted ingress. No automatic HTTPS redirects or unconfigured forwarded-header trust.

## Sources consulted

- [ASP.NET Core timeouts](https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0)
- [ASP.NET Core error handling](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0)
- [OpenTelemetry exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/)
- Live NuGet flat-container indexes selected current stable package versions.

Unresolved questions: product endpoints, identity, persistence, readiness dependencies, production ingress/collector and SLOs remain future product/infrastructure decisions.
