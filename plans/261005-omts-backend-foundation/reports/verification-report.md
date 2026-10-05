# Independent verification

Date: 2026-10-05. Status: DONE_WITH_CONCERNS.

## Verified

- Read README.md, AGENTS.md, docs/deployment-guide.md and scripts/validate.ps1 before execution.
- Isolated SDK installation: version 10.0.401, matches global.json. Prepended SDK directory to PATH and set DOTNET_ROOT.
- Ran exact `./scripts/validate.ps1` once; exit 0. Matches GitHub CI Quality command. Forced locked restore with fresh advisory audit passed; strict Release build passed with 0 warnings/errors; tests 28 passed, 0 failed, 0 skipped; formatting verification passed.
- Published Release application with `dotnet publish src/Omts.Api/Omts.Api.csproj --no-restore --configuration Release --output artifacts/verify/publish`; exit 0.
- Started published DLL in Production on an available loopback port with `AllowedHosts=localhost`. Hidden subprocesses, bounded probes and cleanup limited to owned processes.
- Real Kestrel `/health/live` and `/health/ready`: HTTP 200, application/json, exactly `{"status":"Healthy"}`, no-store cache control, no Server header.
- Real Kestrel `/unknown?secret=private-token`: HTTP 404, application/problem+json, exactly type/title/status/traceId fields, generic Not Found title, no query disclosure, no Server header.
- Published Production startup without AllowedHosts exited nonzero (-532462766) within 10 seconds with expected explicit host validation error.
- Smoke output and logs in ignored `artifacts/verify/`; script exited 0 and disposed/terminated owned processes. No source, project documentation or Git state edits by verifier; this report is the only non-ignored verification output.

## Limits

- Docker daemon unavailable; image build, Linux runtime, non-root container execution and container smoke remain for GitHub CI Container job.
- Initial harness request using localhost timed out; direct IPv4 loopback requests with Host=localhost and proxy disabled passed. This was a harness connectivity correction, not a source change.
- Smoke covers deployed operational endpoints and configuration validation. Exception/timeout/body-limit/readiness-failure behaviors covered by automated tests; no business endpoints or real dependency integration exist in this foundation.

Unresolved: successful remote GitHub CI Quality/Container results still required before claiming container verification.
