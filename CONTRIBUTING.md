# Contributing

## Git workflow

The protected permanent branches are `develop` (integration and default branch), `staging` (UAT), and `main` (production and releasable history). Create short-lived work branches from current `develop`:

- `feat/<ticket>-<slug>`
- `fix/<ticket>-<slug>`
- `refactor/<description>`
- `build/<description>`
- `docs/<description>`
- `release/<semver>` for release preparation, created from `develop` when needed

Open ordinary work PRs into `develop`; keep one logical change per PR. Squash work PRs after required checks pass, independent code-owner approval is recorded, and review conversations are resolved. Rebase only short-lived work branches when needed. Never force-push or delete a permanent branch. Delete merged work branches. Create temporary branches on demand; do not reserve bare `feat`, `fix`, `release`, or `hotfix` branches because they block names such as `release/1.0.0`.

Promote `develop` into `staging`, then approved `staging` into `main`, through PRs merged with **Create a merge commit**. Preserve ancestry between permanent branches; do not squash or rebase promotions. CI runs for pushes and PRs on all three branches. Promotion does not deploy an environment.

Create urgent production fixes from current `main` as `hotfix/<ticket>-<slug>`, then merge the reviewed PR into `main`. Immediately synchronize `main` into both `develop` and `staging` through separate PRs using merge commits, so the fix survives later promotions. The same required checks and review policy apply to hotfixes.

## Commits and PR titles

Use Conventional Commits: `type(scope): imperative description`.

Supported types: `feat`, `fix`, `perf`, `refactor`, `test`, `build`, `ci`, `docs`, `style`, `chore`, `revert`. Examples:

```text
feat(orders): validate order submission
fix(api): return problem details for missing resources
ci: verify container health before publication
chore(release): promote develop to staging
chore(release): promote staging to main
fix(sync): synchronize production hotfix into develop
```

Use `!` and a `BREAKING CHANGE:` footer for intentional incompatible contracts. The squash commit uses the PR title, which CI validates. Include migration and rollback impact in breaking PRs. Do not include generated plan identifiers or review finding codes in code/commit names.

## Before requesting review

Run the commands in README. Add tests for behavioral changes; cover failure and cancellation paths when relevant. Update API documentation, migration notes, and operational docs for changed contracts. Review your diff for credentials and sensitive data. Keep package lock files current when changing dependencies.

CODEOWNERS identifies the initial maintainer. A second maintainer must be added to CODEOWNERS before the initial owner can merge their own PRs, since authors cannot approve their own changes. Expand ownership to teams when the repository moves into an organization. All three permanent branches require one independent code-owner approval, required checks and conversation resolution; applied GitHub settings are recorded in `docs/repository-governance.md`.

## Releases

Use semantic versions `vMAJOR.MINOR.PATCH` once public product contracts exist. Publish a container from an exact verified `main` commit using the manual image workflow. Deploy by image digest and record commit, digest, configuration version and migration version. Container publication does not deploy an environment.
