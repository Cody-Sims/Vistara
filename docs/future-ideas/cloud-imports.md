# Cloud imports

**Status: Future idea — not implemented or committed to a release**

This note explores provider-neutral imports after the current upload, ingest,
job, and reconciliation foundations in the
[product specification](../specification.md) are complete. It does not add a
connector to the current roadmap.

## Verified external capability

Research snapshot: 2026-08-28. Provider APIs and policies can change and must be
revalidated before use.

| Source option | Current feasibility | Verified limitation |
|---|---|---|
| Google Drive Picker + Drive API | Strong for user-selected files | Picker selects IDs; the Drive API downloads bytes. The least-privilege [`drive.file` scope](https://developers.google.com/workspace/drive/api/guides/api-specific-auth) grants access to files the user selects for the app. |
| Broad Google Drive sync | Technically possible, compliance-heavy | Broad read scopes are restricted and can require verification and a security assessment. See [OAuth verification](https://support.google.com/cloud/answer/13464321). |
| Google Photos Picker | User-selected session only; policy go/no-go required | It provides selected media through short-lived base URLs, not whole-library or unattended sync. See the [Picker flow](https://developers.google.com/photos/picker/guides/get-started-picker) and [media retrieval](https://developers.google.com/photos/picker/guides/media-items). |
| Google Photos Library API | Not viable for importing an existing library | Since March 31, 2025, library listing is limited to media created by the requesting app. See [API updates](https://developers.google.com/photos/support/updates). |
| Generic server access to personal iCloud Drive | No supported public OAuth/REST API identified | [CloudKit](https://developer.apple.com/documentation/cloudkit/ckcontainer) operates on app containers, not arbitrary personal iCloud Drive files. |
| Generic server access to personal iCloud Photos | No supported public OAuth/REST API identified | Supported access is client-mediated through [PhotosPicker](https://developer.apple.com/documentation/photosui/photospicker) or PhotoKit authorization on an Apple device. |
| Apple document/directory picker | Viable client-mediated path | The user grants revocable security-scoped access to selected files or a directory. See [document picker](https://developer.apple.com/documentation/uikit/uidocumentpickerviewcontroller) and [directory access](https://developer.apple.com/documentation/uikit/providing-access-to-directories). |
| Apple privacy export | Viable manual archive workflow | Apple lets a user request a copy that may include iCloud files, photos, and videos. See [Get a copy of your Apple data](https://support.apple.com/en-us/102208). |

Google Photos policy prohibits using its APIs to create a substitute
general-purpose photo gallery. Because Vistara is a gallery, a Photos Picker
prototype requires a written policy/legal go/no-go or clarification before
production work:
[Google Photos APIs policy](https://developers.google.com/photos/support/api-policy).

No supported generic server OAuth API currently exists for arbitrary personal
iCloud Photos or iCloud Drive access. Do not scrape iCloud.com, collect Apple
passwords or cookies, automate 2FA, or use private/reverse-engineered APIs.
Supported alternatives are a native companion, a user-selected watched folder,
ordinary browser/file upload, or a user-requested privacy export.

## Vistara recommendation

### Provider-neutral importer

Define a capability-oriented connector boundary rather than provider behavior in
the ingest pipeline:

```text
Authorize / Disconnect
BeginSelection / CompleteSelection
ListObjects(cursor)
OpenRead(object, optional range)
GetMetadata / GetDelta
CancelProviderSession

Capabilities:
selection, listing, rangeRead, durableCursor, checksum, revisions
```

Persist tenant-bound connector accounts, encrypted credential references,
granted scopes, import sessions, provider object/revision identities, cursors,
checkpoints, per-item state, provenance, and retry state. Provider checksums are
hints only; Vistara still computes canonical SHA-256.

Every object must enter the same staging, byte-limit, streaming hash, decoder,
quarantine, immutable promotion, tenant dedupe, audit, and derivative path as an
ordinary upload. A connector must never write accepted blobs or asset rows
directly.

Use `(tenant, provider account, provider object ID, provider revision or picker
session identity)` for import idempotency. Preserve source and rendition/export
method, while keeping raw provider metadata permission-gated. Source deletion or
lost permission does not trash or purge an accepted Vistara copy.

### Consent, tokens, and lifecycle

- Request scopes incrementally and display the actual granted access.
- Prefer one-shot selection without refresh tokens. Request offline access only
  for a separately approved background-sync mode.
- Encrypt refresh tokens and client secrets through an external secret/KMS
  boundary; never store them in source, logs, URLs, job payloads, or metrics.
- Validate OAuth `state`, exact redirect and client configuration, token
  audience, expiry, revocation, and active tenant ownership.
- Disconnect revokes and erases credentials, provider caches, cursors, and
  pending sessions. Retention of already accepted copies must be an explicit,
  separately explained user choice.
- Cancellation stops listing and transfer, closes provider sessions, and aborts
  incomplete destination multipart uploads. It never changes provider data.

### Checkpoints, retries, and dedupe

- Stream provider bytes into destination staging/multipart storage.
- For Drive, checkpoint validated source offsets and destination parts and use
  documented HTTP Range support. If a provider does not document resumability,
  safely restart the object.
- Respect `Retry-After`; retry bounded 429, 5xx, and network failures with jitter.
  Authentication, permission, policy, and unsupported-media failures need
  explicit terminal or user-action states.
- Reconcile expired picker sessions, invalid cursors, stalled jobs, ambiguous
  multipart completion, lost access, and orphaned staging data.
- Never automatically acknowledge an abusive-file warning. Require an explicit
  warning path and quarantine.

### Preferred investigation order

1. Retain browser/device upload as the current path; store no cloud credentials.
2. Prototype Google Drive Picker with `drive.file`, selected supported images,
   one-shot import, and no whole-Drive indexing.
3. Prototype provider-neutral archive ingestion using user-uploaded Apple
   privacy exports and Google Takeout, with format/version detection.
4. Evaluate an iOS/macOS companion using PhotosPicker or document picker. A
   watched, user-selected iCloud Drive folder is the recurring Apple option.
5. Evaluate Google Photos Picker only after policy approval; label downloaded
   bytes as a provider rendition when archival-original guarantees are absent.
6. Consider broad Drive sync only after restricted-scope compliance, operator
   credential ownership, changes-cursor, revocation, and shared-drive research.
7. Do not plan a direct iCloud server connector unless Apple publishes a
   supported API.

## Prototype exit criteria

- Picker modes can access only explicitly selected objects; partial scopes and
  revoked permissions fail closed.
- Cross-tenant tests deny connectors, credentials, sessions, cursors, progress,
  manifests, and imported objects.
- Restarting any import creates no duplicate import record or physical blob;
  accepted duplicates follow the existing tenant-scoped SHA-256 behavior.
- Interrupted Drive transfers resume from verified checkpoints; unsupported
  providers safely restart without corrupting or exposing partial content.
- Contract tests cover 401, 403, 404/lost permission, 429/`Retry-After`, 5xx,
  timeout, expired picker URL, and ambiguous destination completion.
- Cancellation leaves no provider mutation or orphaned destination multipart
  upload. Worker termination loses no accepted item and exposes no quarantined
  item.
- Disconnect/account deletion revokes tokens and erases credentials and cached
  provider data. The UI separately states what happens to accepted copies.
- Logs, errors, traces, and metric labels contain no token, provider URL,
  filename, raw metadata, hash, or tenant identifier.
- Google Photos has a recorded policy/legal approval before any production
  implementation.
- Apple paths pass permission-revocation and device/offline tests without
  relying on private APIs or credentials.

## Risks, unknowns, and non-goals

Risks include high-value token theft, cross-tenant connector confusion, provider
policy changes, DLP/download restrictions, quota exhaustion, archive bombs,
metadata leakage, duplicate inflation, import-as-SSRF, and users assuming a
provider rendition is an archival original.

Unknowns include the OAuth client ownership model for self-hosted instances,
Google Photos policy eligibility, supported archive versions, Apple companion
distribution, checksum and resume guarantees by provider, credential KMS
requirements, and UX for disconnect versus deletion of accepted copies.

Non-goals are source deletion, bidirectional synchronization, password-based
connectors, scraping, private APIs, bypassing provider policy, importing
unsupported documents/video as images, treating provider metadata as trusted,
or allowing importer/model/MCP output to mutate blobs or authorize purge.
