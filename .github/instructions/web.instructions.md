---
description: React, TypeScript, accessibility, and browser-boundary rules
applyTo: 'src/Vistara.Web/**'
---

# Web instructions

- Keep the Web project frontend-only and communicate with backend capabilities through reviewed HTTP/OpenAPI contracts.
- Use React, TypeScript, Vite, React Router data APIs, and TanStack Query according to the approved gallery architecture.
- Store resumable upload metadata in IndexedDB without persisting credentials or signed grants.
- Preserve keyboard, screen-reader, touch, zoom, reduced-motion, focus-restoration, and URL-addressable navigation behavior.
- Treat the API image as the production SPA host; a GitHub Pages build is a static preview and must not imply a production backend.
- Run `npm --prefix src/Vistara.Web run check` for completed Web changes.
