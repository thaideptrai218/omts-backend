# Contributing

## Git workflow

Use trunk-based development. `main` should always build and be releasable. Create short-lived branches from current `main`:

- `feat/<issue>-<description>`
- `fix/<issue>-<description>`
- `refactor/<description>`
- `build/<description>`
- `docs/<description>`
- `hotfix/<issue>-<description>`

Open a pull request; keep one logical change per PR. Rebase your branch when needed; never force-push `main`. Merge using squash after CI passes and review conversations are resolved. Delete merged branches. Apply urgent fixes through the same checks; define a documented incident exception if emergency bypass is needed.

## Commits and PR titles

Use Conventional Commits: `type(scope): imperative description`.

Supported types: `feat`, `fix`, `perf`, `refactor`, `test`, `build`, `ci`, `docs`, `style`, `chore`, `revert`. Examples:

```text
feat(orders): validate order submission
fix(api): return problem details for missing resources
ci: verify container health before publication
```

Use `!` and a `BREAKING CHANGE:` footer for intentional incompatible contracts. The squash commit uses the PR title, which CI validates. Include migration and rollback impact in breaking PRs. Do not include generated plan identifiers or review finding codes in code/commit names.

## Before requesting review

Run the commands in README. Add tests for behavioral changes; cover failure and cancellation paths when relevant. Update API documentation, migration notes, and operational docs for changed contracts. Review your diff for credentials and sensitive data. Keep package lock files current when changing dependencies.

CODEOWNERS identifies the initial maintainer. Expand it to teams when the repository moves into an organization. One independent approval, required CI and conversation resolution are the intended main-branch policy; actual GitHub enforcement and plan limitations are recorded in `docs/repository-governance.md`.

## Releases

Use semantic versions `vMAJOR.MINOR.PATCH` once public product contracts exist. Publish a container from an exact verified `main` commit using the manual image workflow. Deploy by image digest and record commit, digest, configuration version and migration version. Container publication does not deploy an environment.
