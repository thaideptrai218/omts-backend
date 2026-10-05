# Repository governance

Verified: 2026-10-05, using GitHub REST API through `gh`.

## Applied settings

- Public repository `thaideptrai218/omts-backend`, default branch `main`, explicitly selected by the owner.
- Squash merges enabled; merge commits and rebase merges disabled.
- Squash commit uses PR title/body; merged head branches deleted automatically.
- Branch update button enabled; issues enabled; wiki disabled.
- Actions enabled with GitHub-owned actions and an allowlist of Docker build/login actions. Workflow references use verified immutable commit SHAs; repository-level SHA pinning is required.
- Default workflow token permissions are read-only; Actions cannot approve PRs. Publication grants package write permission only to its publication job.
- Dependabot vulnerability alerts and automated security updates enabled. Scheduled dependency update configuration is versioned in `.github/dependabot.yml`.

## Branch policy

The intended `main` policy is versioned in [.github/branch-protection-main.json](../.github/branch-protection-main.json): successful Quality/Container/PR-title checks, up-to-date branch, independent approval, code-owner review, dismissed stale reviews, final-push approval, resolved conversations, linear history, no force-pushes and no deletions. Admins are also subject to the policy.

The owner selected public visibility after GitHub rejected private branch protection on the current account plan. The verified bootstrap PR is merged before applying this policy, since the sole initial owner cannot approve their own PR. Subsequent changes require an independent reviewer.

Apply and verify the versioned policy:

```sh
gh api --method PUT repos/thaideptrai218/omts-backend/branches/main/protection --input .github/branch-protection-main.json
gh api repos/thaideptrai218/omts-backend/branches/main/protection
```

Assign a second maintainer/reviewer for future changes. For an organization, use team-based CODEOWNERS and least privilege role assignments. Review emergency access and rule changes through an audited process.

## Security controls

CI audits all NuGet dependencies and fails on advisory warnings. Dependency updates are reviewed PRs. GitHub secret scanning and push protection are enabled. Public repository CodeQL and dependency review workflows supplement the build gates. Product authorization and data security still require domain-specific design and tests.

## Publication

Container publication is a manual workflow on current `main`, gated by successful push CI for the exact commit. It grants scoped package permissions, refuses existing release tags, emits provenance/SBOM and smoke-tests a candidate by digest before promoting the identical manifest to an immutable commit tag. Publication records and verifies the digest. Deployment remains a separate action; no production environment has been configured.
