# Self-hosted save/state sync (EBS#13): a versioned object store + a client SyncBackend

**Status:** approved 2026-08-12, ready for planning.

## Goal

Give the audience running the self-hosted server a save/state sync backend that isn't Google Drive.
The server offers a tiny, dumb, **versioned object store** (it never interprets payloads); the media
client gains a `SyncBackend` seam so the existing Google Drive path becomes one of two backends, "My
server" the other, over the **same** merge rules and category carve-outs. A token-URL backend also
sidesteps Google-OAuth-on-TV (the client's Android sign-in gap).

Two repos: the object-store API in `EverythingBoxServer` (C#/.NET), the client seam in the media
client (Qt/C++). The **wire contract is the shared boundary** and is frozen by Increment A.

## Design principle (from the issue, confirmed by the code)

**Intelligence stays client-side.** The client's `CloudMerge` (newest-ts resume, union-with-tombstones
tags, device-namespaced stats) and `SaveSyncPlan` are already pure and transport-neutral, and the
sync/never-sync category decisions live in two static predicates (`isPerItemStoreKey`,
`isDeviceLocalKey`) *above* the transport. So the backend is transport-only; the server stores blobs +
stamps and rejects stale writes — it never merges.

## The shared wire contract (frozen by Increment A)

All routes under the existing **token path-prefix** (`/{token}/…`; presence under the prefix *is* the
auth — same model as every other route). A `{namespace}` path segment scopes a client profile
(token-scoped, not identity-scoped — no accounts). Enabled by config; when disabled the routes are not
mapped.

An **object** = `{ key, version, meta, size, deleted, modifiedUtc }` + bytes. `version` is a
server-assigned opaque per-write stamp (an `ETag`). `meta` is a small opaque client-supplied string the
server stores and echoes verbatim (this carries the client's existing content `stateHash`; the server
never reads it).

- **`GET /{token}/sync/{ns}`** → `200 { "objects": [ { key, version, meta, size, deleted, modifiedUtc } … ] }`.
  Lists every object including tombstones (`deleted:true`). HTTP-200 = "reached" (the client must
  distinguish reached-but-empty from unreachable — a network failure is never read as "empty").
- **`GET /{token}/sync/{ns}/{**key}`** → `200` bytes, headers `ETag: "<version>"`, `X-Sync-Meta: <meta>`.
  `404` if absent or tombstoned.
- **`PUT /{token}/sync/{ns}/{**key}`** — body = raw bytes; optional `If-Match: "<version>"` /
  `If-None-Match: *`; optional `X-Sync-Meta: <meta>`. Semantics:
  - `If-Match: "<v>"` → succeed iff the current version == v (a tombstone's version counts, allowing
    un-delete); else **`412`**.
  - `If-None-Match: *` → succeed iff the object is absent or tombstoned; else **`412`**.
  - neither → unconditional (used for migration/push-all).
  - On success → `204` + `ETag: "<newVersion>"`. Quota exceeded → **`507`**. Bad namespace/key or body
    over the per-object cap → `400`.
- **`DELETE /{token}/sync/{ns}/{**key}`** — optional `If-Match`. Writes a **tombstone** (mark deleted,
  assign a new version, free the blob bytes; the key stays in the index so other devices observe the
  deletion). Conditional semantics as PUT. → `204` + `ETag`. `412` on mismatch.

Standard HTTP conditional-request semantics, so the client maps its compare-and-swap onto `If-Match`
directly. The server rejects stale writes; it never merges.

## Increment A — the server object store (`EverythingBoxServer`)

New host-level feature (not a plugin — it's first-class like the browse/stream/files routes).

- **`SyncStore`** (`EverythingBox.Server/Sync/`) — the durable store. Per namespace: a subdirectory with
  blob files + one `index.json` (`{ key → { version, meta, size, deleted, modifiedUtc, blob } }`).
  - **Keys are hashed, never pathed.** A client key → SHA-256 → flat blob filename; the real key lives
    only in the index. A key can therefore be any string (incl. `/`) with zero path-traversal risk. The
    URL uses a catch-all `{**key}`.
  - **Namespace** is a single validated segment (`^[A-Za-z0-9._-]{1,64}$`, reject `.`/`..`) → a subdir.
    (A namespace escape is the only path risk; validate it, and additionally confirm containment with
    `SafeLocalFileServer.IsContained`/`ResolveReal` against the sync root as belt-and-suspenders.)
  - **Atomic writes:** blob and index written temp-then-`File.Move(overwrite:true)`, mirroring
    `FileResolverCache.SetAsync`. A **per-namespace async lock** (`SemaphoreSlim` keyed by namespace)
    serializes mutations so "check version → write → bump version → rewrite index" is atomic (correct CAS).
  - **Version stamp:** an opaque token generated per write (e.g. a random 16-byte base64url). Uniqueness
    is all CAS needs; ordering isn't required (the client's own content ts drives merge).
  - **Quota:** per-namespace byte cap from config → `507` when a PUT would exceed `sum(sizes) - oldSize
    + newSize`. Enforced after streaming the body to a temp file (and a cheap `Content-Length` pre-check).
  - **Tombstone GC is out of scope v1** (tombstones accrete; documented).
- **`SyncEndpoints.MapSync(this WebApplication, string prefix)`** (mirrors `AddonEndpoints`) — the four
  routes above, returning `Results.Json`/status codes; PUT reads `http.Request.Body` to a temp file
  (the first write route in the host); 507 via `Results.StatusCode(507)`, 412 via
  `Results.StatusCode(412)`. Mapped from `Program.cs` **only when `config.Sync.Enabled`**.
- **`SyncConfig`** on `ServerConfig` (`sealed class`, like `DownloadConfig`): `Enabled=false`,
  `Directory` (+ a `ResolvedSyncDirectory` in the `ResolvedFilesCacheDir` idiom), `PerNamespaceQuotaBytes`
  (default e.g. 256 MB), `MaxObjectBytes` (default e.g. 64 MB). Absent section → disabled, degrade-not-refuse.
- **Tests** (`EverythingBox.Server.Tests`): a `TokenPluginServerFactory`-style factory writing a `Sync`
  config, driving real PUT/GET/DELETE/list over `CreateClient()`; assert: put→get round-trips bytes +
  ETag + meta; list shows versions/sizes/tombstones; `If-Match` stale → 412; `If-None-Match:*` create vs
  conflict; delete → tombstone (list shows deleted, get → 404); quota → 507; bad namespace/traversal key
  → 400/contained; unauthenticated (no token prefix) → 404; concurrent writers to one key serialize (no
  lost index). Plus `SyncStore` unit tests for the hashing/containment/atomic-index logic. No process, no
  network beyond the in-memory `WebApplicationFactory`.
- **API note:** these are host routes, not the plugin contract — **no `ServerApi` version bump**.

## Increment B — extract the `SyncBackend` seam in the client (Qt/C++), behavior-preserving

Pure refactor: create the seam without changing behavior, so the Drive path is unchanged and the probes
stay green. This de-risks Increment C.

- **`SyncBackend`** (new abstract, `native/src/core/`) — the transport interface, exactly the six
  primitives `CloudSync` already declares `virtual` plus their reached/`listOk` semantics:
  `ensureFolder`, `findFile` (→ `{listOk, id, modifiedIso, stateHash}`), `uploadFile(name, bytes,
  stateHash) → id`, `downloadFile(id)`, `findFolderNamed`, `renameFile`. These are already the tests'
  fake seam (`FakeCloud` overrides exactly these), so promoting them to an injected interface reuses the
  entire test harness.
- **`DriveSyncBackend`** — move the current inline Drive HTTP (OAuth, the REST calls at
  `CloudSync.cpp:310-1011`) into this class implementing `SyncBackend`. `CloudSync` keeps the
  orchestration (`checkStatus`/`applyRemote`/`pushLocal`/`pullProgress`/`pushProgress`, bundle
  build/apply, the carve-out predicates, fingerprint) and calls `backend_->…`. `signIn`/`signOut`/
  account state move with Drive (they're Drive-specific) — exposed via a backend capability
  (`requiresSignIn()` / `signIn()`), so "My server" can report no-OAuth.
- **Behavior-preserving:** `probe_cloudmerge`, `probe_savesync` (its FakeCloud now implements
  `SyncBackend`), `probe_sync`, `probe_onboarding` all pass unchanged. This increment adds NO new user
  feature — it only relocates code behind the interface.

## Increment C — the "My server" backend + selector + pairing + migration (Qt/C++)

- **`ServerSyncBackend : SyncBackend`** — implements the six primitives over Increment A's HTTP:
  `ensureFolder`→ensure the namespace (a no-op GET/implicit-create); `findFile(name)`→`GET /sync/{ns}`
  then find `key==name`, returning `{listOk=(reached), id=key, modifiedIso=modifiedUtc, stateHash=meta}`;
  `uploadFile(name,bytes,stateHash)`→`PUT …/{name}` with `X-Sync-Meta: stateHash` and `If-Match`/
  `If-None-Match` from the last-known version (stale → surface as the existing CAS refusal);
  `downloadFile(id)`→`GET …/{id}`; `renameFile`→PUT-new + DELETE-old (or a documented no-op if unused by
  the server path). The client's `stateHash` model is preserved verbatim (it rides `X-Sync-Meta`); the
  server's `ETag`/`If-Match` strengthens the previously client-only optimistic CAS.
- **Backend selector + config** under the device-local `cloud/` prefix (never synced, inherits the
  carve-out for free): `cloud/backend` (`"drive"` | `"server"`), `cloud/server/url`, `cloud/server/token`,
  `cloud/server/namespace` (default from the current profile id). One backend active at a time.
- **Pairing UX** in `MainWindow::openCloudSync()` — a backend chooser; for "My server", URL + token
  fields (token in a password field), mirroring the client's existing server-pointing entry style. The
  token is a device-local secret (stored under `cloud/`, never in the bundle/fingerprint).
- **Switching backends is a migration, not a divergence:** on switch, push-all local state to the new
  target (the issue's "one sync master" rule), then adopt the new baseline. No silent two-master state.
- **Same categories + carve-outs:** unchanged — `buildSettingsJson`/`applySettingsJson`/
  `isDeviceLocalKey`/`isPerItemStoreKey` sit above the backend and are reused as-is.
- **Tests:** a `probe_serversync` (or extend `probe_savesync`) with a fake HTTP transport (or a
  `ServerSyncBackend` pointed at an in-process stub) driving `syncNow`/`pushLocal`/`applyRemote` +
  `SaveSync` end-to-end with no sockets; assert list/get/put-if-match/tombstone map correctly and a
  stale version is refused. Merge-over-server (`probe_cloudmerge` semantics) unchanged since the merge is
  above the backend. Live end-to-end is verified later via the `EB_UITEST` harness against a real server
  (a manual/hardware pass, noted).

## What binds

- **Frozen contract:** Increment A defines the wire; B and C consume it. No server-side merging.
- **Server never reads payloads** — blobs are opaque; `meta` is echoed, not interpreted. E2E encryption
  is therefore a clean later client-side wrapper (out of scope v1; trust model is LAN/VPN/tunnel, #10).
- **Security:** namespace validated + containment-checked; keys hashed (no traversal); token is the only
  gate (path-prefix); the server token is a device-local client secret, never synced.
- **Behavior-preserving client refactor** (Increment B) gated by the existing probes; Drive path
  unchanged.
- **Cleanliness:** no external content-source name; `RepositoryCleanlinessTests` green. No new server
  NuGet package. Server routes add no `ServerApi` bump.

## Out of scope (v1, per the issue)

- Multi-user / OIDC accounts (namespaces are token-scoped).
- End-to-end encryption (a later client-side wrapper; the server already never reads payloads).
- Tombstone garbage collection on the server.
- Sync of the server's own state (it has none).
- Changing the merge rules or categories (they are reused verbatim).

## Done when

- **A:** the server exposes the four sync routes, token-authenticated, per-namespace, quota-bounded, with
  atomic CAS; full integration + unit tests green; `RepositoryCleanlinessTests` green; no API bump.
- **B:** `SyncBackend`/`DriveSyncBackend` extracted; the Drive path behaves identically; all client sync
  probes green.
- **C:** `ServerSyncBackend` + selector + pairing + migration land; a network-free probe proves the
  server backend + SaveSync round-trip and CAS; save/state sync works over the self-hosted backend
  (live-verified later via `EB_UITEST`). #13 can then be closed.
