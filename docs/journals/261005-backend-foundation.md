---
date: 2026-10-05
---
# Backend foundation

## Context

Create an enterprise backend repository for OMTS without an existing application or defined business domain. Scope: verified ASP.NET Core hosting, delivery controls and contribution conventions. See [architecture](../system-architecture.md), [governance](../repository-governance.md) and [roadmap](../project-roadmap.md).

## What happened

- Added a small ASP.NET Core service using .NET SDK 10.0.401 and runtime 10.0.12, structured logging, OpenTelemetry, health checks and strict Production host configuration.
- Added package locks, 28 passing tests, non-root container checks, dependency/security checks and workflows pinned to immutable action SHAs.
- Linux exposed a failure in negative startup tests: deferred hosting in `WebApplicationFactory` observed a disposed provider after failed `app.Run`. ASP.NET Core/runtime v10.0.12 source confirmed the lifecycle behavior.
- Replaced those negative tests with direct Production hosts using the same `AddPlatform` registration. Assertions require `OptionsValidationException`, the expected validation reason and a host that never started. Production behavior was unchanged.
- [PR #1](https://github.com/thaideptrai218/omts-backend/pull/1) passed its checks and merged by squash at `646500dad1f69eba165ccca95b1793c7c0fafce1`.
- [Main CI](https://github.com/thaideptrai218/omts-backend/actions/runs/37271374748), [main CodeQL](https://github.com/thaideptrai218/omts-backend/actions/runs/37271374797) and [GHCR publication](https://github.com/thaideptrai218/omts-backend/actions/runs/37271462466) passed. CodeQL reported no open alerts.

## Reflection

Startup rejection requires testing both the validation failure and the absence of a running host. A framework fixture lifecycle can obscure that failure; direct hosting preserved the application registration and strengthened the observable assertions.

A verified hosting foundation supplies operational defaults, but product authorization, persistence and production operations still need explicit requirements and evidence.

## Decisions

- Keep one service; introduce modules at real domain boundaries instead of creating empty layers.
- Pin servicing versions and dependency locks; review updates through CI.
- Use a public repository, explicitly selected by the owner after private branch protection returned HTTP 403 on the account plan. Retain source ownership without granting an open-source license.
- Merge verified bootstrap code and verification documentation before applying independent-review protection. Future changes require an independent CODEOWNER.
- Separate publication from deployment. Smoke-test a candidate by digest, then promote the identical manifest with SBOM and provenance; end-to-end publication passed with digest equality.

## Next

Apply and read back the versioned branch policy as the final provisioning operation; assign an independent CODEOWNER for future changes. The verified image and source evidence are recorded in the [completion report](../../plans/261005-omts-backend-foundation/reports/completion-report.md).

Unresolved decisions: OMTS capabilities and API consumers; identity and authorization; persistence, migrations and integrations; hosting, ingress, secrets and deployment identity; SLOs, capacity and incident response; retention, backup/restore and disaster recovery.
