# Platform API boundary

`src/api/generated` is produced from the reviewed gallery OpenAPI manifest and
must not be edited. The session, account, and platform administration screens
need routes that the gallery manifest does not describe yet, so this folder
holds a small hand-written client that follows the same conventions:
`fetch` injection, `same-origin` credentials, `ApiResponse<T>` results, and
`VistaraApiError` problem details.

## Routes used

| Route | Screen | Source |
|---|---|---|
| `GET /api/v1/me` | session bootstrap | specification §8 core routes |
| `POST /api/v1/auth/login` | `/login` | specification §8 core routes |
| `POST /api/v1/auth/logout` | account menu | specification §8 core routes |
| `GET /api/v1/capabilities` | `/login`, `/settings` | specification §8 core routes |
| `PATCH /api/v1/me/preferences` | `/settings` | assumed |
| `GET/PATCH /api/v1/admin/users` | `/admin/users` | assumed |
| `GET /api/v1/admin/storage` | `/admin/storage` | assumed |
| `GET /api/v1/admin/jobs`, `POST .../retry`, `POST .../cancel` | `/admin/jobs` | assumed |
| `GET/PATCH /api/v1/admin/policies` | `/admin/policies` | assumed |
| `GET /api/v1/admin/audit` | `/admin/audit` | assumed |

## Assumptions

- Administration lives under the `/api/v1/admin` prefix because the
  specification requires separate platform-administration routes, policies, and
  audience from tenant gallery routes.
- Unsafe cookie-authenticated requests send the antiforgery token bootstrapped
  by `GET /api/v1/me` in the `X-CSRF-TOKEN` header. The token is held in memory
  only; no credential or signed grant is persisted.
- Optimistic concurrency uses `If-Match` with the entity tag returned by the
  matching read, and the API answers a stale edit with `409`.
- Collections answer with the shared `CursorPage<T>` shape.

When these routes reach the reviewed manifest, replace the calls here with the
generated client and delete the corresponding models.
