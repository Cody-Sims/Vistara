# Model Context Protocol server

**Status: Future idea — not implemented or committed to a release**

This note explores a future Vistara-specific Model Context Protocol (MCP)
adapter. Vistara currently has no MCP endpoint or MCP package dependency.

## Verified external capability

Research snapshot: 2026-08-28. Revalidate the protocol and SDK before use.

- The current [MCP specification](https://modelcontextprotocol.io/specification/2026-07-28)
  defines resources, prompts, tools, discovery, pagination, transports, and
  authorization. Resources are application-controlled context; tools are
  model-controlled operations whose invocation a human should be able to deny.
- Standard transports include local
  [stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
  and remote
  [Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).
- Tool input and structured output use JSON Schema; annotations are untrusted
  hints, not authorization controls:
  [tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools).
- Remote authorization is defined by the MCP
  [authorization specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
  MCP does not make a tool safe merely because a client authenticated.
- C# is an official
  [Tier 1 SDK](https://modelcontextprotocol.io/docs/2026-07-28/sdk). Protocol and
  SDK versions must be pinned and revalidated when investigation begins.

## Vistara recommendation

### Surface: read-only first

Begin with safe, tenant-relative resources and bounded read tools:

- capabilities and policy;
- safe normalized asset metadata and provenance;
- albums, tags, duplicate-review sets, AI suggestion state, proposals, and jobs;
- gallery search, metadata inspection, duplicate review, suggestion listing,
  and job status.

Resources must exclude original bytes, raw private metadata, credentials,
storage keys, signed URLs, and cross-tenant identifiers. Search should be a
bounded tool using opaque, principal-bound cursors rather than an enumerable
resource for every asset.

Expose no prompts initially. A later prompt experiment must be user-selected,
read-only, and explicitly quote OCR, filenames, captions, metadata, and provider
content as untrusted data.

### Propose, approve, execute

Mutation should be introduced in distinct capability stages:

1. `propose` resolves explicit asset/version references and persists an
   immutable canonical proposal.
2. `dry-run` returns bounded per-item diffs, warnings, permissions, holds,
   shared-link impact, uncertainty, and an expiring proposal hash.
3. A trusted Vistara UI obtains explicit human approval, with recent
   reauthentication for high-impact operations.
4. `execute` validates tenant, actor, OAuth client, scope, proposal hash,
   expiry, approval, policy version, object authorization, versions, holds, and
   idempotency again.
5. `undo` applies a stored inverse only when current versions permit it.

Initially executable operations should be limited to canonical metadata,
album/tag organization, and movement to reversible Trash. There must be no MCP
tool, scope, proposal operation, indirect route, or owner bypass for permanent
purge. No model or MCP output directly mutates blobs or authorizes purge.

### OAuth, tenancy, and deployment

- Prefer remote Streamable HTTP integrated through the official ASP.NET Core
  SDK, while keeping business policy in reusable Vistara application services.
- Treat Vistara as an OAuth resource server and integrate a separately selected
  authorization server; the current specification does not make Vistara a
  general OAuth authorization server.
- Validate issuer, exact audience/resource, expiry, revocation, client identity,
  user identity, active tenant membership, and granular scope on every request.
- Derive tenant only from authenticated membership/token context. Bind cursors,
  resources, proposals, approvals, jobs, and undo tokens to tenant, principal,
  and client, then reauthorize each use.
- Never pass the incoming MCP token to an AI or cloud provider.
- API keys, if supported at all, are for explicitly configured headless
  automation with narrower MCP-specific scopes. Reversible execution should be
  disabled by default.
- A local stdio adapter should be sandboxed, log only to stderr, lack direct
  database/storage credentials, and have no shell, filesystem browsing,
  arbitrary networking, or cloud-credential capability.

### Injection and exfiltration controls

Treat image-visible text, OCR, filenames, Exif/IPTC/XMP, captions, AI results,
import manifests, and provider responses as untrusted data, never instructions.
Keep them in typed escaped fields; do not interpolate them into system or tool
instructions. Model output cannot select tool names, scopes, tenant IDs, URLs,
storage keys, or executable expressions.

Compile any natural-language rule only to an allowlisted declarative form, then
evaluate it deterministically. Reject arbitrary URL fetch, generic HTTP, SQL,
shell, filesystem, storage administration, transform strings, original
download, and credential tools. Apply egress allowlists and the existing
private/link-local/cloud-metadata protections to any later connector.

Follow the official MCP
[security best practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices)
for confused-deputy, token-passthrough, SSRF, state-handle, and local-server
risks.

## Compatibility and jobs

Support one pinned current protocol version first. Add older-version
compatibility only after client telemetry and regression tests justify it; never
weaken stateless, tenant-bound application semantics to emulate connection
state.

Every long operation returns a durable Vistara job ID. The optional MCP
[Tasks extension](https://modelcontextprotocol.io/extensions/tasks/overview)
may map to that same record when negotiated, but request-scoped progress and
client support cannot be the sole source of truth.

## Prototype exit criteria

- Official SDK conformance and Inspector scenarios pass for the pinned versions.
- Every tool, resource, cursor, proposal, approval, job, and undo token has
  positive and negative cross-tenant tests.
- Issuer, audience/resource, PKCE, scope, consent, revocation, expiry, replay,
  stale-version, and idempotency failures close safely.
- A hidden-instruction corpus across OCR, metadata, filenames, and provider
  content causes zero unauthorized mutation or data exfiltration.
- The discoverable surface and every indirect execution path contain no
  permanent-purge operation.
- Proposal tampering or changed policy/assets requires a new dry run and
  approval; duplicate execution is idempotent.
- Reversible actions preserve audit and produce tested per-item undo/conflict
  results.
- Tokens, raw metadata, untrusted text, signed URLs, and credentials do not
  appear in logs, errors, traces, or metric labels.
- Fault injection proves durable recovery without untracked partial mutation.

## Risks, unknowns, and non-goals

Risks include prompt injection, confused-deputy authorization, excessive client
trust, bearer-token theft, schema drift, optional-client capability mismatch,
large result exfiltration, and models presenting destructive annotations as
proof of safety.

Unknowns include the authorization server and consent UX, client adoption of the
current protocol and Tasks extension, exact resource/tool schemas, rate and size
limits, local adapter distribution, and whether prompts should ever be exposed.

Non-goals are a generic administration MCP, arbitrary storage/network/code
execution, direct original download, autonomous acceptance, autonomous Trash,
permanent purge, or using tool annotations as authorization.
