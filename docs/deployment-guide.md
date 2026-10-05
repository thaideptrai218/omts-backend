# Deployment and operations

## Local runtime

Install the pinned SDK and run the README verification commands. `dotnet run --project src/Omts.Api` uses Development launch settings. For production behavior use an explicit environment and host allowlist:

```sh
ASPNETCORE_ENVIRONMENT=Production AllowedHosts=localhost ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/Omts.Api --no-launch-profile
```

The example uses POSIX shell syntax; in PowerShell set the corresponding `$env:` variables before running dotnet. Mandatory configuration is validated at startup.

## Configuration

| Variable | Behavior |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | Set `Production` in hosted environments. |
| `AllowedHosts` | Required outside Development; explicit hostname/IP values separated by `;`. No wildcard, URL or port. Include probe hosts used by the orchestrator. |
| `ASPNETCORE_HTTP_PORTS` | Container default `8080`; ingress must target this port. |
| `Hosting__MaxRequestBodyBytes` | Default 1048576; accepted range 1 through 104857600. |
| `Hosting__RequestTimeoutSeconds` | Default 30; accepted range 1 through 300. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional collector endpoint; enables trace/metric export when set. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Standard OTLP protocol configuration, matching the collector. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Optional collector credentials; inject as a secret, never commit. |

Without an OTLP endpoint, structured JSON logs still go to stdout. Restrict telemetry collection and retention to approved data. Do not expose a collector publicly without authentication.

## Container

```sh
docker build -t omts-backend:local .
docker run --rm --name omts-api -p 8080:8080 -e AllowedHosts=localhost omts-backend:local
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
```

The image uses the official pinned .NET SDK/runtime servicing tags and a non-root runtime user. Review tag changes in Dependabot PRs. For environment delivery, resolve the published image to an immutable digest and promote the same digest through staging/production.

Run with a read-only root filesystem, a writable bounded `/tmp` mount, dropped Linux capabilities, `no-new-privileges`, and explicit CPU/memory limits in the chosen orchestrator. Validate these settings against actual application dependencies before launch. Let the host deliver SIGTERM and allow a grace period for active work to drain.

## Health and ingress

`GET /health/live` returns process health. `GET /health/ready` returns 200 while ready and 503 when unavailable. Responses expose status only. Add readiness checks for real dependencies before relying on readiness to serve business traffic. Restrict health access through ingress/network policy.

Terminate TLS at trusted ingress and enforce HTTPS there. The service does not assume trusted forwarded headers; configure explicit proxy/network trust before using forwarded client IPs or scheme for security decisions. Host filtering is a request allowlist, not an authentication control. Avoid publishing port 8080 directly to the internet.

## Publication and deployment

The GitHub container workflow accepts manual dispatch on `main` and verifies successful CI for the exact commit. It builds a candidate image with provenance/SBOM, pulls and smoke-tests that exact digest, then promotes the unchanged manifest to `sha-<full-commit>` and verifies digest equality. The workflow summary records the digest. It creates an image only. Select a deployment provider and configure environment approvals, OIDC identity and rollback before adding deployment jobs.

Each run uses a `candidate-<run-id>-<attempt>` tag. Delete failed candidate-only package versions after investigation; never delete a version that also carries a retained release SHA tag. Successful candidate and release tags can reference the same package version. Keep approved rollback digests and define registry retention before production.

Before production: establish DNS/TLS, identity policies, selected dependencies, secret store, SLOs, load-test evidence, alerts and on-call ownership. For persisted data establish tested backup/restore, migration review and recovery procedures.

## Rollback

Record image digest, commit, runtime configuration and migration version for every deployment. Roll back to the previous verified digest through the same deployment controls. Check readiness and error/latency indicators before restoring traffic. Database changes require an independently verified compatibility/recovery plan; image rollback alone cannot undo data changes.
