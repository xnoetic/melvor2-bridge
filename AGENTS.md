# AGENTS.md

Notes for AI coding agents and contributors working in this repository.

## Verification does not depend on GitHub CI

Since 2026-11-25 this repository does not use GitHub Actions for verification, nor for building, releasing or deploying (publishing GitHub Pages is the only exception). `.github/workflows/` may only hold a workflow that publishes GitHub Pages; do not add any other workflow.

- Run tests, checks and builds locally (or on a verification host through calltable `ct`) before pushing.
- Commit and push notes state the commands actually run and where; never write "CI passed" and do not wait for CI results.
