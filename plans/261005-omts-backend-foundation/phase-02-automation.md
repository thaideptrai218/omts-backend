---
phase: 2
title: Automation
status: completed
effort: ''
---

# Phase 2: Automation

## Overview

Create build/release workflows, container packaging and contribution controls.

## Implementation Steps

1. Add Dockerfile, ignore rules and local validation scripts.
2. Add CI for locked restore, formatting, build, tests, vulnerability audit, container build and smoke tests.
3. Add manual image publication after CI, Dependabot, PR/issue templates, CODEOWNERS and conventional PR title gate.
4. Create private repo through gh; configure squash merges, least privilege Actions and branch rules when supported.

## Success Criteria

- [ ] CI runs successfully on GitHub.
- [ ] Container executes as non-root and health probes pass.
- [ ] Actions pinned to immutable SHAs; publication permissions isolated.

## Files and ownership

Automation worker owns `.github/`, `Dockerfile`, `.dockerignore`, `.gitignore`, `.gitattributes`, scripts. Controller owns GitHub mutations. No live deployment target selected.
