---
description: GitHub Actions security, release, artifact, and deployment rules
applyTo: '.github/workflows/**,.github/dependabot.yml'
---

# Workflow instructions

- Pin third-party actions to full commit SHAs and retain a human-readable version comment beside each pin.
- Declare least-privilege permissions, a finite timeout for every job, and workflow concurrency explicitly.
- Do not use `pull_request_target`; untrusted pull-request code must never run with elevated credentials.
- Bound artifact paths and retention, and define behavior when expected files are absent.
- Restrict Pages deployment to `main`, where it publishes only the static Web preview.
- Grant package write permission only to release- or tag-triggered publication workflows.
- Keep checkout credentials disabled unless a reviewed step demonstrably requires them.
- Never expose repository secrets to pull-request-triggered workflows; live-credential suites run only on schedule or dispatch.
- Keep `.github/dependabot.yml` covering Actions, NuGet, every npm manifest, and deployment images on a recurring bounded schedule.
