# Future ideas

**Status: Future idea — not implemented or committed to a release**

These notes preserve research directions without changing the approved
[product specification and roadmap](../specification.md). They are not release
promises, implementation plans, or evidence that an API, connector, model, or
protocol integration exists.

## Topics

- [Metadata and AI-assisted editing](metadata-and-ai-editing.md)
- [Model Context Protocol server](mcp-server.md)
- [Cloud imports](cloud-imports.md)

## Shared principles

Any future investigation should preserve the current architecture:

- Originals remain immutable. Metadata edits create versioned canonical state;
  they do not rewrite original blobs.
- Private-by-default access, tenant isolation, least privilege, auditability,
  and reversible Trash remain mandatory.
- Model, provider, importer, and MCP output is untrusted data. It cannot directly
  mutate blobs, select authorization scope, or authorize permanent purge.
- AI and automation create reviewable suggestions or immutable proposals.
  Deterministic application policy, object-level authorization, optimistic
  concurrency, and explicit human approval govern execution.
- External capability, Vistara recommendation, and unresolved questions must be
  documented separately. Provider documentation and policy must be rechecked
  before a prototype begins.
- Experiments should be capability-gated and removable. They must not create a
  dependency for the MVP roadmap.

## Shared investigation sequence

1. Revalidate external APIs, standards, terms, and current Vistara prerequisites.
2. Write a threat model and privacy/data-flow review.
3. Prototype against synthetic or dedicated test data.
4. Measure the topic-specific exit criteria in these notes.
5. Make a separate reviewed product and architecture decision. Until then, the
   idea remains uncommitted.

## Shared non-goals

- Changing current roadmap dependencies, dates, or release scope.
- Treating provider availability as a product commitment.
- Scraping services, using passwords or private APIs, or bypassing provider
  review.
- Autonomous deletion, permanent purge, storage mutation, or authorization by a
  model or MCP client.
- Storing credentials, signed URLs, raw private metadata, or untrusted content
  in logs or metric labels.
