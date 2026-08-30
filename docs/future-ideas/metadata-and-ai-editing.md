# Metadata and AI-assisted editing

**Status: Future idea — not implemented or committed to a release**

This note explores interoperable metadata and safe AI-assisted editing after the
foundations in the [product specification](../specification.md) exist. It does
not add metadata editing or AI behavior to the current roadmap.

## Verified external capability

Research snapshot: 2026-08-28. Revalidate versions and mappings before use.

- [Exif](https://www.cipa.jp/std/documents/download_e.html?DC-008-Translation-2023-E)
  represents capture and technical fields commonly embedded by cameras.
- The [IPTC Photo Metadata Standard](https://iptc.org/standards/photo-metadata/iptc-standard/)
  defines interoperable descriptive, administrative, rights, location, and
  digital-source properties. IPTC publishes mappings for XMP and legacy IIM.
- [XMP, ISO 16684-1](https://www.iso.org/standard/75163.html) is an extensible
  metadata representation and packaging model. XMP alone does not decide which
  conflicting value is authoritative.
- The [IPTC Photo Metadata Mapping Guidelines](https://iptc.org/std/photometadata/documentation/mappingguidelines/)
  document current field mappings. The legacy
  [Metadata Working Group guidelines](https://iptc.org/thirdparty/#mwg) remain
  useful historical reconciliation guidance, but neither guarantees that every
  application preserves or maps fields consistently.
- [C2PA Content Credentials](https://spec.c2pa.org/specifications/)
  can cryptographically bind signed provenance assertions to media. They can
  show whether a manifest verifies; they do not prove that every assertion is
  true, prevent stripping, or replace access control.

Embedded metadata is mutable and is often stripped by publishing pipelines.
Neither Exif, IPTC, nor ordinary XMP is a trustworthy authorization or identity
source.

## Vistara recommendation

### Canonical layers

Keep four explicit layers rather than resolving every value into one opaque
metadata document:

1. **Fact layer** — immutable observations from the accepted asset revision:
   byte hash, detected format, dimensions, decoder results, and parsed source
   metadata with parser/version provenance.
2. **Effective canonical layer** — normalized values used by search, display,
   sorting, and APIs, such as capture time, title, description, creator, rights,
   rating, and location.
3. **Override layer** — versioned user or authorized-import edits. An override
   can replace an effective value without altering the fact layer or original
   blob.
4. **Proposal layer** — unaccepted AI, import, rules-engine, or MCP suggestions.
   Proposals never become canonical merely because a confidence threshold was
   met.

For every effective field, retain value, normalized type, source layer, source
namespace/property, source revision, actor or provider, timestamp, confidence
semantics where relevant, and supersession history.

### Reconciliation

- Normalize known Exif/IPTC/XMP properties into typed fields while retaining a
  permission-gated raw source record.
- Use a documented field-by-field precedence table, informed by IPTC mappings
  and MWG guidance. Do not use one global “XMP wins” rule.
- Prefer explicit authorized user overrides for descriptive intent. Preserve
  capture-derived facts and conflicts rather than silently overwriting them.
- Represent partial dates, local capture time, offset, precision, language,
  controlled vocabulary identifiers, and units explicitly.
- Record exports as new representations. Never rewrite the immutable original
  merely to embed a metadata edit.
- Treat provider checksums, timestamps, filenames, and embedded author claims as
  evidence with provenance, not as identity or integrity proof.

### Privacy, viewer, filtering, and indexing

Classify fields before exposing or indexing them:

| Class | Examples | Default handling |
|---|---|---|
| Public-safe | dimensions, format, approved title | Eligible for ordinary viewer/API output |
| Private | precise GPS, device serial, contact details | Permission-gated; absent from derivatives |
| Sensitive-derived | faces, OCR, inferred location, captions | Opt-in, provenance-visible, separately erasable |
| Operational | hashes, storage/provider identifiers | Never public; avoid logs and metric labels |

The viewer should show the effective value and its source, expose conflicts and
history to authorized users, and distinguish user-authored, imported, and
AI-proposed content. Search indexes should contain only the fields the index
principal is permitted to discover. Public/share indexes need separate safe
projections; they must not depend on query-time redaction of a private index.
Reindexing must be revision-bound, idempotent, and capable of erasing sensitive
derived fields.

### AI proposal and undo safety

- AI adapters receive no blob mutation, lifecycle, authorization, or purge
  capability.
- Inputs bind to exact asset revisions, consent, model digest, preprocessing and
  configuration digest, taxonomy, and budget.
- Outputs are immutable typed artifacts. OCR, captions, filenames, embedded
  metadata, and visible image text are untrusted data, never instructions.
- Acceptance uses a deterministic allowlisted operation, `If-Match`-style
  version checks, object-level authorization, audit, and a stored inverse where
  reversal is meaningful.
- Bulk proposals show per-field diffs, uncertainty, conflicts, affected assets,
  and privacy consequences before confirmation.
- Undo creates a new canonical version; it does not erase history. A stale or
  conflicting inverse fails closed and requires review.
- Moving an asset to Trash may only follow the existing human-confirmed
  proposal firewall. Permanent purge remains separate and cannot be authorized
  by model or MCP output.

## Phased investigation

1. **Interoperability corpus:** parser normalization and field-precedence tests
   across representative Exif, IPTC IIM, and XMP samples.
2. **Canonical editing:** single-field and bulk user overrides, history,
   conflict handling, export projection, and undo without AI.
3. **Local proposals:** fixed-taxonomy tags, OCR, and captions stored only as
   reviewable artifacts.
4. **Search experiments:** permission-aware metadata and hybrid search with
   erasure/reindex tests.
5. **Optional provenance export:** evaluate IPTC digital-source properties and
   C2PA signing without claiming that either proves semantic truth.
6. **Optional hosted models:** only after separate consent, retention,
   residency, licensing, redaction, and budget review.

## Prototype exit criteria

A proposal may advance only if a reproducible test report shows:

- Round-trip preservation or an explicit, field-level loss report for the
  supported metadata corpus.
- Deterministic precedence results and zero silent overwrite of conflicting
  facts or user overrides.
- Cross-tenant and share-view tests expose no private or sensitive-derived
  fields through APIs, indexes, exports, caches, or logs.
- Every accepted edit records actor, source, before/after values, revision, and
  an auditable inverse or documented non-reversibility.
- A prompt-injection corpus causes zero unauthorized canonical, blob, lifecycle,
  or authorization mutation.
- Stale-version, replay, partial-failure, model-retry, and index-rebuild tests
  fail closed or produce explicit per-item results.
- Sensitive-derived data can be erased and proven absent from indexes and
  generated representations within a documented bound.
- Any auto-visible AI field meets a separately approved representative
  evaluation threshold; the current specification’s 98% precision requirement
  remains the minimum for that future decision.

## Risks, unknowns, and non-goals

Risks include accidental GPS/identity disclosure, parser vulnerabilities,
metadata bombs, language and cultural bias, false authorship, model licensing,
index leakage, provenance stripping, and users over-trusting confidence or C2PA
verification.

Unknowns include the supported namespace/version matrix, export policy, rights
workflow, controlled vocabularies, localization model, C2PA trust-store
operation, hosted-provider choices, and whether any AI field should ever become
visible without item-level review.

Non-goals are rewriting originals, claiming embedded metadata is truthful,
identity or face recognition, sensitive-attribute inference, training on tenant
media by default, autonomous moderation, and autonomous Trash or purge.
