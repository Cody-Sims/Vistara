# Hosted identity and Azure bootstrap

**Status:** Planned post-MVP work - not implemented or committed to a release

## Outcome

An operator can deploy Vistara to Azure with `azd up`, open the hosted URL, sign
in with an approved Microsoft Entra ID account, and complete the initial
workspace setup without copying application secrets into the browser.

The flow must remain self-hosted and recoverable:

1. `azd` and Bicep provision the Azure resources and identities.
2. A migration job completes before the API and worker become active.
3. The browser uses OpenID Connect authorization code flow with PKCE.
4. An allowlisted first identity may atomically create the initial workspace
   owner.
5. Later sign-ins resolve existing memberships and never create an owner
   implicitly.

This plan extends, rather than replaces, local password sign-in. A separately
protected local recovery owner remains available when Entra ID is unavailable
or misconfigured.

## Security boundaries

### Interactive Entra ID sign-in

- Use authorization code flow with PKCE, exact redirect URIs, `state`, `nonce`,
  issuer, audience, tenant, signature, expiry, and token-type validation.
- Accept only configured Entra tenant IDs. Multi-tenant or personal Microsoft
  accounts are disabled unless an operator explicitly enables and reviews them.
- Use stable Entra object and tenant IDs as external identity keys. Email and
  display name are profile attributes, not authorization identifiers.
- Create the Vistara browser session only after the external identity and active
  tenant membership are resolved. Do not expose Entra access or refresh tokens
  to application JavaScript or Vistara logs.
- Preserve the existing confused-credential rejection, cookie protections,
  antiforgery checks, tenant isolation, session expiry, audit, and revocation
  behavior.

### Guarded first-login provisioning

Automatic setup must never become open signup.

- Deployment configuration declares an allowlist of initial Entra object IDs or
  group IDs and the one Entra tenant ID they belong to.
- The first eligible sign-in claims the existing database-enforced bootstrap
  singleton inside the same transaction that creates the tenant, user,
  external identity, owner membership, and audit record.
- Email-domain matching alone is insufficient. A verified email may improve the
  display profile but cannot grant ownership.
- Simultaneous eligible sign-ins produce one winner. Every other attempt fails
  closed and can sign in only after an existing owner grants membership.
- Once setup closes, changing the deployment allowlist cannot create another
  owner.
- Recovery procedures require a local operator action; there is no hidden
  default account or support bypass.

### Azure credentials and managed identity

- API and worker use separate managed identities and least-privilege role
  assignments.
- Azure Blob access uses managed identity by default. Account keys and SAS
  tokens remain explicit fallback modes.
- PostgreSQL adds an Entra token provider through a reviewed
  `NpgsqlDataSource`/periodic password provider. The migration job, API, and
  worker use separate database principals.
- Key Vault stores any unavoidable application secret. Bicep and `azd` outputs
  expose secret references, never secret values.
- Browser cloud onboarding continues to validate candidates transiently and
  never persists a credential.

## Azure deployment scope

The first supported template should provision:

- one resource group and a cost-management budget/alert configuration;
- Azure Container Apps environment;
- API and worker container apps with separate managed identities;
- a one-shot migration Container Apps job;
- Azure Database for PostgreSQL Flexible Server;
- Azure StorageV2 account and private Blob container;
- Azure Key Vault and role assignments;
- container registry configuration for public GHCR, private GHCR, or ACR;
- health probes, scaling bounds, logs, and OpenTelemetry configuration;
- optional custom hostname and TLS inputs without making them mandatory for an
  evaluation deployment.

The template must be parameterized for region, resource names, budget amount,
minimum and maximum replicas, database SKU, storage redundancy, and teardown
retention. Defaults target an evaluation instance and must not claim to fit
within a changing free-service allowance.

## Proposed work items

| ID | Outcome | Depends on | Verification |
|---|---|---|---|
| `CLOUD-01` | Threat model, data-flow review, and accepted architecture decision | Current auth and deployment docs | Security review covers account takeover, token theft, redirect abuse, bootstrap races, and supply chain |
| `CLOUD-02` | Entra issuer/client configuration and external identity persistence | CLOUD-01 | Provider and migration tests for tenant/object identity uniqueness and revocation |
| `CLOUD-03` | OIDC login, callback, logout, and cookie-session handoff | CLOUD-02 | Browser tests cover PKCE, state, nonce, wrong tenant, replay, cancellation, and callback errors |
| `CLOUD-04` | Database-enforced first-login owner bootstrap | CLOUD-02 | Concurrent eligible sign-ins create exactly one tenant owner; all other identities fail closed |
| `CLOUD-05` | Managed-identity PostgreSQL connections for migration, API, and worker | CLOUD-01 | Live Azure fixture rotates tokens without process restart and preserves separate database roles |
| `CLOUD-06` | Bicep modules for data, identity, compute, secrets, networking, and observability | CLOUD-01, CLOUD-05 | What-if, lint, policy, and ephemeral deployment tests |
| `CLOUD-07` | `azd` environment, hooks, migration gate, health verification, and teardown | CLOUD-03, CLOUD-04, CLOUD-06 | Fresh `azd up` reaches healthy and `azd down` leaves only explicitly retained data |
| `CLOUD-08` | Operator guide, incident recovery, cost controls, and rollback | CLOUD-07 | A new operator follows the guide without repository knowledge or secret values in shell history |

`CLOUD-02`, `CLOUD-05`, and `CLOUD-06` may proceed in parallel after
`CLOUD-01`. Browser integration waits for the identity contract; deployment
automation waits for the database identity and infrastructure modules.

## Acceptance criteria

- A clean Azure subscription can reach a healthy Vistara deployment from one
  `azd up` command plus interactive Azure login and explicit parameter choices.
- No secret value appears in source, Bicep output, `azd` output, browser
  storage, URL, logs, traces, metrics, or screenshots.
- The configured initial Entra identity can create exactly one owner; an
  unlisted identity, wrong tenant, replay, or concurrent loser cannot.
- A returning member can sign in with Entra ID and receives the same tenant,
  role, scopes, session protections, and audit behavior as local sign-in.
- API, worker, and migration identities have only their required Blob, Key
  Vault, and PostgreSQL permissions.
- Migration failure prevents API/worker rollout; health failure prevents a
  successful deployment result.
- Token rotation and transient Entra/Azure outages recover without storing
  long-lived cloud credentials in Vistara.
- `azd down` reports retained resources and costs explicitly and does not delete
  protected data without a separate confirmation.
- The existing local Compose deployment and local-password recovery path remain
  functional.

## Non-goals

- Open public registration or email-domain-only owner grants.
- Automatic import from OneDrive, iCloud, Google Drive, or other personal cloud
  libraries.
- Automatic migration of existing media between storage providers.
- Storing Entra, Azure, or PostgreSQL credentials in the browser.
- Making the Azure template the only supported deployment.
- Claiming a fixed Azure free-tier cost or quantity.

## Rollout and rollback

1. Ship Entra sign-in behind disabled-by-default configuration while local
   password recovery remains enabled.
2. Validate the identity flow against an isolated Entra test tenant.
3. Publish preview Bicep/`azd` templates that create disposable environments.
4. Run live deployment, migration, backup/restore, rotation, and teardown
   drills.
5. Mark the template supported only after cost, security, and operator reviews.

Rollback disables the Entra handler and returns to local sign-in without
deleting external identity links. Infrastructure rollback uses the previous
container digests and migration compatibility rules; it never rolls a database
back by restoring an older application image alone.

## Decisions still required

- OIDC client library and server-side token-cache strategy.
- Object-ID versus group-ID bootstrap allowlists and group overage handling.
- Public Container Apps ingress versus private networking plus an edge.
- ACR versus GHCR as the default image source.
- PostgreSQL private access, DNS, and token-provider implementation details.
- Whether `azd down` retains PostgreSQL and Blob resources by default.
