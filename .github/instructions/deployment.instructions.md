---
description: Container, Compose, migration, and runtime deployment rules
applyTo: 'deploy/**'
---

# Deployment instructions

- Build distinct API and Worker images from the same release; include the compiled SPA only in the API image.
- Pin base images by digest, run final containers as non-root, and retain only the runtime files required by each role.
- Keep SQLite single-host and local-volume only; production examples use PostgreSQL and support external object storage.
- Run migration bundles before API rollout with production runtime identities lacking schema-owner permissions.
- Bound worker scratch space and capabilities, and limit egress to configured database, storage, telemetry, and explicitly enabled provider endpoints.
- Validate Compose topology and both container builds after deployment changes.
