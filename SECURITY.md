# Security policy

## Supported versions

Security fixes target current `main` and the latest supported production release after releases exist. Use supported .NET 10 servicing updates and update pinned dependencies, SDK and images through reviewed PRs.

## Reporting

Do not disclose credentials or exploit details in public issues. Use [GitHub private vulnerability reporting](https://github.com/thaideptrai218/omts-backend/security/advisories/new), which is enabled for this repository. The owner must establish incident escalation and response targets before production launch.

## Handling secrets

Store local secrets using .NET user-secrets and production secrets in the selected managed secret store. Inject configuration at runtime. Do not commit dotenv files, tokens, credentials, private certificates or production data. Rotate any exposed secret immediately and audit its use; removing a file does not revoke a secret.

## Service controls

Terminate TLS at a trusted ingress; limit access to operational endpoints. Configure explicit hostnames, resource limits, trusted proxy handling and identity before exposing product routes. Authenticate and authorize every product capability with an approved identity provider and least privilege policies. Avoid logging request bodies, bearer tokens or personal data. Security-relevant events need a separate retention and access policy.

Dependabot, CI package audit, CodeQL and dependency review are configured. Secret scanning and push protection are enabled; `docs/repository-governance.md` records the observed state. Before release, threat-model real data flows, test access controls and define backup/restore and incident procedures for actual dependencies.
