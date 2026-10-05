# System architecture

## Current foundation

One ASP.NET Core 10 service, `src/Omts.Api`, owns HTTP hosting and operational behavior. Tests in `tests/` verify the actual hosting pipeline. The solution intentionally starts with a small deployable service; feature/module boundaries will follow the OMTS domain model.

The runtime uses structured console logs, OpenTelemetry, health checks, standardized error responses, bounded request processing and validated configuration. Liveness represents process availability; readiness represents configured dependencies. Add readiness checks when introducing dependencies and never use liveness to restart the service merely because a downstream service is unavailable.

The container runs as a non-root user on port 8080. A trusted ingress terminates TLS. CI verifies source, dependencies and runtime behavior; the publication workflow pushes a verified container to GHCR. No environment is deployed automatically.

## Extension rules

Keep business rules independent of HTTP and storage details. Introduce domain/application boundaries for meaningful capabilities, and infrastructure adapters for selected dependencies. Prefer a modular monolith until independent scaling, ownership or release needs justify service extraction.

Define persistence, transactions, concurrency, tenant isolation and migration strategy before writing product data. Define identity, role/policy ownership, audit events and data retention before exposing product capabilities. Redis, messaging, Kubernetes and CQRS are decisions driven by measured requirements.

## Architecture decisions

- .NET 10 LTS is the supported foundation; servicing versions are pinned and reviewed.
- Public repository, selected by the owner to enable enforced branch protection; source ownership retained, no open-source license granted.
- Trunk-based development with short-lived branches, squash PRs and Conventional Commits.
- Container publication is separate from deployment; infrastructure/provider remains a product decision.
- Empty architectural layers and fabricated sample business data are excluded from the foundation.
