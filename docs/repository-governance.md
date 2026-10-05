# Repository governance

Verified: 2026-10-05, using GitHub REST API through `gh`.

## Applied settings

- Public repository `thaideptrai218/omts-backend`, default branch `develop` for integration. Permanent `staging` tracks UAT and `main` tracks production.
- Squash merges and merge commits enabled; rebase merges disabled. Work PRs into `develop` use squash; promotions and synchronization between permanent branches use merge commits to preserve ancestry.
- Squash and merge commits use PR title/body. Merged temporary head branches are deleted automatically; permanent branches are protected against deletion.
- Branch update button enabled; issues enabled; wiki disabled.
- Actions enabled with GitHub-owned actions and an allowlist of Docker build/login actions. Workflow references use verified immutable commit SHAs; repository-level SHA pinning is required.
- Default workflow token permissions are read-only; Actions cannot approve PRs. Publication grants package write permission only to its publication job.
- Dependabot vulnerability alerts and automated security updates enabled. Scheduled dependency update configuration is versioned in `.github/dependabot.yml`.

## Branch policy

The applied policies are versioned in [.github/branch-protection-main.json](../.github/branch-protection-main.json), [.github/branch-protection-develop.json](../.github/branch-protection-develop.json), and [.github/branch-protection-staging.json](../.github/branch-protection-staging.json). All require successful `Quality`, `Container`, `Conventional PR title`, `CodeQL`, and `Dependency review` checks; an up-to-date branch; one independent code-owner approval; dismissed stale reviews; approval after the final push; and resolved conversations. Admins are also subject to these policies. Force-pushes and deletions are disabled. Linear history is not required, allowing promotion and hotfix synchronization merge commits.

The owner selected public visibility after GitHub rejected private branch protection on the current account plan. Verified bootstrap code and verification documentation were merged before protecting `main`. The branch workflow bootstrap was committed to newly created `develop` and copied to `staging` before protecting those branches; `main` retained its existing commit. Subsequent changes require an independent reviewer.

Apply and verify the versioned policy:

```sh
gh api --method PUT repos/thaideptrai218/omts-backend/branches/main/protection --input .github/branch-protection-main.json
gh api --method PUT repos/thaideptrai218/omts-backend/branches/develop/protection --input .github/branch-protection-develop.json
gh api --method PUT repos/thaideptrai218/omts-backend/branches/staging/protection --input .github/branch-protection-staging.json
gh api repos/thaideptrai218/omts-backend/branches/main/protection
gh api repos/thaideptrai218/omts-backend/branches/develop/protection
gh api repos/thaideptrai218/omts-backend/branches/staging/protection
```

Assign a second maintainer and add them to CODEOWNERS for future changes authored by the initial owner. For an organization, use team-based CODEOWNERS and least privilege role assignments. Review emergency access and rule changes through an audited process.

Create `feat/<ticket>-<slug>`, `fix/<ticket>-<slug>`, and `release/<semver>` branches from `develop` only when work starts. Squash reviewed work into `develop`, then promote `develop` to `staging` and `staging` to `main` with merge commits. Create `hotfix/<ticket>-<slug>` from `main`, merge the reviewed fix into `main`, then synchronize `main` into `develop` and `staging` through merge PRs. Use Conventional Commit promotion titles such as `chore(release): promote staging to main`. Permanent branch names describe source workflow stages; they do not imply configured deployments.

## Security controls

CI audits all NuGet dependencies and fails on advisory warnings. Dependency updates are reviewed PRs. GitHub secret scanning and push protection are enabled. Public repository CodeQL and dependency review workflows supplement the build gates. Product authorization and data security still require domain-specific design and tests.

## Publication

Container publication is a manual workflow on current `main`, gated by successful push CI for the exact commit. It grants scoped package permissions, refuses existing release tags, emits provenance/SBOM and smoke-tests a candidate by digest before promoting the identical manifest to an immutable commit tag. Publication records and verifies the digest. Deployment remains a separate action; no production environment has been configured.
