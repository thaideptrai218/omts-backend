---
title: OMTS backend production foundation
description: >-
  Create a public GitHub repository and verified .NET 10 hosting and delivery
  foundation.
status: completed
priority: P2
branch: ''
tags: []
blockedBy: []
blocks: []
created: '2026-10-05T05:47:27.179Z'
createdBy: 'ck:plan'
source: skill
---

# OMTS backend production foundation

## Overview

Deliver `thaideptrai218/omts-backend` with .NET 10 API, automated verification, a non-root container, Git conventions, and accurate operational documentation. The owner selected public visibility after GitHub rejected private protection on the current plan. No existing project or contracts were found. Reviewed scope: build/test/format, health and error contracts, safe configuration, GitHub CI and repository controls. Domain features and live deployment need product and infrastructure decisions.

## Phases

| Phase | Name | Status |
|-------|------|--------|
| 1 | [Platform](./phase-01-platform.md) | Completed |
| 2 | [Automation](./phase-02-automation.md) | Completed |
| 3 | [Verification](./phase-03-verification.md) | Completed |

## Dependencies

Platform and automation may run in parallel with separate file ownership. Verification depends on both. Apply enforced branch protection after merging the verified bootstrap PR; subsequent PRs require an independent reviewer.

## Acceptance criteria

- SDK pinned to 10.0.401; locked dependency restore, Release build, integration tests and formatting pass.
- Liveness/readiness endpoints and generic RFC 9457 problem responses verified in production mode.
- Container builds and smoke tests in GitHub Actions; release publishes immutable commit tags to GHCR only after successful CI.
- Main uses PR conventions, squash merges, CODEOWNERS, dependency updates, and supported branch protection.
- No secrets committed; production readiness gaps explicitly documented.
