# Roadmap

Updated: 2026-10-05.

## Foundation

Track implementation and verification in [the foundation plan](../plans/261005-omts-backend-foundation/plan.md). The repository baseline includes operational API hosting, package locks, automated checks, non-root container delivery and contribution conventions.

## Production release decisions

1. Define OMTS business capabilities, API consumers, ownership and acceptance criteria.
2. Select identity provider and authorization model; verify access controls and tenant isolation where applicable.
3. Select persistence and integrations; add real dependency health checks, integration tests and migrations.
4. Select hosting, ingress, DNS/TLS and secret manager; configure deployment identity through OIDC and environment approvals.
5. Define SLOs, load tests, capacity, alerts, dashboards, on-call and incident response.
6. Establish data retention, audit trails, backup/restore drills, disaster recovery and migration rollback.
7. Assign independent reviewers and organizational CODEOWNERS for protected `main`.

Release requires evidence for the applicable decisions above. No live environment is currently provisioned.
