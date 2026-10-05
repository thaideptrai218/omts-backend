# Foundation verification evidence

Date: 2026-10-05.

## Delivered

Public repository [thaideptrai218/omts-backend](https://github.com/thaideptrai218/omts-backend), explicitly approved by the owner. .NET 10 API, strict configuration, operational health, generic errors, JSON logging, optional OTLP, package locks, non-root image, security checks and trunk-based Git conventions.

## Verified source and delivery

- Local SDK 10.0.401: audited locked restore, strict Release build with zero warnings/errors, 28/28 tests, formatting, real published Kestrel checks and invalid-host startup rejection passed.
- [Bootstrap PR #1](https://github.com/thaideptrai218/omts-backend/pull/1) passed Quality, Container, CodeQL, Dependency review and Conventional PR title checks, then merged by squash.
- Verified source commit: `646500dad1f69eba165ccca95b1793c7c0fafce1`.
- [Push CI](https://github.com/thaideptrai218/omts-backend/actions/runs/37271374748) and [CodeQL](https://github.com/thaideptrai218/omts-backend/actions/runs/37271374797) passed on that main commit. CodeQL open alerts: zero at verification time.
- [Image publication](https://github.com/thaideptrai218/omts-backend/actions/runs/37271462466) passed: unique candidate with SBOM/provenance, exact-digest container health tests, unchanged manifest promotion and digest equality assertion.

Verified image:

```text
ghcr.io/thaideptrai218/omts-backend:sha-646500dad1f69eba165ccca95b1793c7c0fafce1
ghcr.io/thaideptrai218/omts-backend@sha256:d038bc96add015ceba6c73ece1534239c49ad3adf2dd780ddb0492970b4dc872
```

## Repository provisioning

API readback confirmed public visibility, squash-only merges, automatic merged-branch deletion, read-only default workflow tokens, disabled Actions PR approval, SHA pinning, selected-action allowlist, Dependabot updates, private vulnerability reporting, secret scanning and push protection. The final provisioning operation is to apply and read back the versioned five-check independent-review main policy after verified bootstrap documentation merges. This report records the verified delivery evidence; the GitHub protection API is authoritative for current enforcement.

## Linux startup test correction

The first Linux CI exposed a deferred-host fixture race for startup failures. Negative tests now start the shared Production service configuration directly and assert exact validation errors, reasons and absence of ApplicationStarted; no arbitrary exception acceptance or parallelization suppression. Production behavior was unchanged. Corrected Linux CI passed all 28 tests.

Primary sources: [deferred host startup](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Mvc/Mvc.Testing/src/DeferredHostBuilder.cs), [failed host disposal](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/HostingAbstractionsHostExtensions.cs), [startup validation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs).

## Limits and unresolved decisions

No production environment is deployed. Product endpoints, identity/authorization, persistence/migrations, integrations, hosting/ingress/secrets, SLOs, load tests, incident response and data recovery need real requirements and validation. External OTLP collection is not verified. Future owner-authored PRs need an additional independent CODEOWNER.
