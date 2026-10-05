# Repository engineering rules

Read README.md and relevant docs before changing code. Follow docs/code-standards.md and CONTRIBUTING.md.

- Keep changes focused. Add modules when there is a real domain boundary; avoid empty layers.
- Preserve contracts unless the requested change explicitly updates them.
- Run locked restore, strict Release build, relevant tests and formatting. Fix failures without weakening checks.
- Never commit secrets or production data. Do not log sensitive request bodies.
- Update operational documentation when configuration, endpoints, delivery or runtime behavior changes.
- Follow Conventional Commits and use PRs into main.
- Report verification results and unresolved decisions accurately. Do not describe an undeployed foundation as a complete production service.
