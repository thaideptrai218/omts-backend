# Automation report

Status: DONE_WITH_CONCERNS

## Delivered

- CI on main pushes and main PRs: locked audited restore, strict Release build, tests, formatting, image build and container smoke.
- Portable PowerShell validation and smoke scripts; temporary loopback port, non-root check, bounded health retries, failure logs and container cleanup.
- SDK 10.0.401 / ASP.NET 10.0.12 multi-stage Dockerfile, non-root APP_UID, production port 8080 and restricted build context.
- Manual main-only GHCR publication gated on latest successful exact-commit push CI. Existing commit tags are rejected.
- Publication pushes a unique candidate with provenance and SBOM, pulls and smoke-tests its digest, then copies the same manifest to the permanent SHA tag and asserts digest equality.
- Conventional PR title gate reads title through JavaScript context, without shell interpolation.
- CODEOWNERS, PR template, bug/feature forms, NuGet/actions/Docker Dependabot and versioned main branch protection intent.
- Public-repository CodeQL C# analysis on main/PR/weekly schedule and dependency review rejecting new low-or-higher vulnerabilities.

## Verification

- `actionlint` 1.7.12 passed all five workflows.
- All three embedded github-script JavaScript bodies parsed with Node 24.15.0; valid and invalid conventional title examples passed.
- PowerShell parser passed both scripts.
- Actual `scripts/validate.ps1` passed SDK 10.0.401 forced locked audited restore, Release build with zero warnings/errors, all 28 tests, and format verification.
- Every action stable tag was resolved through `gh api` to its commit SHA, including dereferencing annotated tags.
- Docker official documentation confirms single-source manifest copying and `--prefer-index=false` preservation; promotion inspects registry JSON digest after tagging.

## Immutable action pins

| Action | Verified stable tag | Commit SHA |
|---|---|---|
| actions/checkout | v7.0.1 | 3d3c42e5aac5ba805825da76410c181273ba90b1 |
| actions/setup-dotnet | v6.0.0 | a98b56852c35b8e3190ac28c8c2271da59106c68 |
| actions/github-script | v9.0.0 | 3a2844b7e9c422d3c10d287c895573f7108da1b3 |
| docker/login-action | v4.6.0 | dbcb813823bdd20940b903addbd779551569679f |
| docker/setup-buildx-action | v4.4.1 | f87e5991a6d7451dcb8d9637bfbc97413f497069 |
| docker/build-push-action | v7.4.0 | c3c9e263c25d99ce0380d002d59b67737d91b0dc |
| github/codeql-action | v4.38.2 | 2892aa5e19bbd11bc0cff5427e3b750a04d9e3c2 |
| actions/dependency-review-action | v5.0.0 | a1d282b36b6f3519aa1f3fc636f609c47dddb294 |

## Remaining verification and operation notes

Local Docker Desktop daemon was unavailable at implementation time. Controller will verify container, GitHub CI/security jobs, and end-to-end publication on GitHub before applying final required checks.

Required job names: `Quality`, `Container`, `Conventional PR title`, `CodeQL`, `Dependency review`.

Candidates use `candidate-<run-id>-<attempt>` and remain in GHCR for investigation. Delete abandoned candidate-only package versions after reviewing failure logs. Never delete a package version carrying a retained `sha-` tag: successful candidate and release tags refer to the same version and digest. The workflow summary records candidate and verified release references.

No Git operations, repository settings, deployment, source changes, or ordinary project documentation edits were made by this worker.

Unresolved questions: none.
