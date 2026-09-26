# Public Ephemeral KV Store — Implementation Handoff

**Audience:** coding agent implementing this service.
**Stack:** C# / .NET 10, `BPUtil` (`SimpleHttp.HttpServer`) — **not** Kestrel, **not** ASP.NET Core, **not** IIS.
**Storage:** LiteDB for metadata, flat files for values.
**Target host:** Oracle Cloud Always Free Ubuntu VM (ARM Ampere A1), behind Cloudflare.
**Budget:** ~1 GB RAM, 5–20 GB disk.

Companion file: `kvstore_install.sh` (Linux installer, modeled on `webproxy_install.sh`).

---

## 1. Purpose

A deliberately public, account-free, ephemeral key/value service. It exists so that
self-hosted and serverless web apps — which cannot hold a secret API key, because the
key would ship to user devices — can move small blobs of data between a user's devices
without filesystems, email attachments, or cloud accounts.

Canonical flow: user taps "Export settings" on device A → app uploads a blob under a
high-entropy key → app displays the key as a QR code and/or word phrase → user enters
it on device B → app downloads and imports.

**The security model is the key itself.** Anyone who knows a key can read, overwrite,
or delete that value. This is intentional and must not be "fixed" with accounts,
tokens, or API keys.

---

## 2. Non-goals (do not implement)

- No user accounts, signup, login, API keys, or per-app credentials on the public API.
- No `GET /{key}` retrieval. **All KV operations are POST.** This prevents the service
  from being used as a link-shareable file host.
- No `Content-Type` awareness for stored values. Values are opaque bytes. The server
  never sniffs, never renders, never echoes a caller-supplied content type.
- No listing, enumeration, or search of stored keys — on the public API *or* the admin UI.
- No server-side encryption of values (see §13 — encryption is a client convention).
- **No logging of client IP addresses anywhere.** See §11.
- No clustering, replication, or HA. Single node.
- No ASP.NET Core dependency of any kind.

---

## 3. Deployment topology

```
Internet ──> Cloudflare (proxied DNS) ──> Cloudflare Tunnel ──> 127.0.0.1:8080  Public API
                                                                <vpn-ip>:8081    Admin UI (NOT tunneled)
```

- **Public API** binds `127.0.0.1:8080`, exposed only via `cloudflared`.
- **Admin UI** binds a separate `HttpServer` endpoint on a separate port. It must **not**
  go through Cloudflare. Bind it to a WireGuard/Tailscale interface address, or to
  loopback plus an SSH tunnel. Never expose it to the public internet.
- Both endpoints run in one process. `BPUtil.SimpleHttp.HttpServer` can bind an http
  and an https port; if two independent listeners are needed, instantiate two
  `HttpServer` subclasses.
- Oracle VCN security list / NSG plus host `nftables`: deny all inbound except SSH and
  the VPN interface. Cloudflare Tunnel is outbound-only, so **no inbound HTTP ports are
  required at all**.

---

## 4. Client IP resolution — BPUtil change required

Rate limiting depends on this and it is security-critical.

### 4.1 Patch `BPUtil/SimpleHttp/SimpleHttpServer.cs`

Cloudflare's `CF-Connecting-IP` has semantics identical to `X-Real-IP`: a single IP
address, no comma-separated chain, always overwritten by Cloudflare. The existing
`X-Real-Ip` code path can be reused verbatim.

**(a)** Next to `XRealIPHeader` (~line 1452), add:

```csharp
/// <summary>
/// If true, the IP address of remote hosts will be learned from the HTTP header named
/// "CF-Connecting-IP".  Also requires the method <see cref="IsTrustedProxyServer"/> to
/// return true.  This header is set by Cloudflare and, unlike "X-Forwarded-For",
/// contains exactly one address and is always overwritten by Cloudflare.
/// </summary>
public bool CFConnectingIPHeader = false;
```

**(b)** In `CommonRequestProcessing()` (~line 518), add a block inside the existing
`if (srv.IsTrustedProxyServer(...))` body. **Place it after the `XForwardedForHeader`
block.** The existing blocks apply in source order and each overwrites
`RemoteIPAddress`, so whichever runs last wins; `CF-Connecting-IP` must take precedence
because Cloudflare also appends to `X-Forwarded-For`, whose leftmost entry is
client-controlled.

```csharp
if (srv.CFConnectingIPHeader)
{
    string headerValue = Request.Headers.Get("CF-Connecting-IP");
    if (!string.IsNullOrWhiteSpace(headerValue))
    {
        headerValue = headerValue.Trim();
        if (IPAddress.TryParse(headerValue, out IPAddress addr))
            RemoteIPAddress = addr;
    }
}
```

**(c)** Update the XML doc on `IsTrustedProxyServer` (~line 1840) to mention
`CFConnectingIPHeader` alongside the existing flags.

**(d)** Add a unit test asserting that `CF-Connecting-IP` wins over both `X-Real-Ip` and
`X-Forwarded-For` when all three are present and all three flags are enabled.

### 4.2 Service configuration

- Enable **only** `CFConnectingIPHeader`. Leave `XRealIPHeader` and
  `XForwardedForHeader` set to `false` on the public listener.
- Override `IsTrustedProxyServer` to return `true` only for `IPAddress.Loopback` /
  `IPv6Loopback` (the local `cloudflared` process).
- If the deployment is ever changed to direct inbound instead of Tunnel, the trusted
  set becomes Cloudflare's published ranges from `https://www.cloudflare.com/ips-v4`
  and `/ips-v6`, refreshed daily, **and** the host firewall must drop all
  non-Cloudflare traffic to the HTTP port. Origin IPs are discoverable via
  certificate-transparency scanning; a header allowlist without a firewall rule is not
  protection.
- On the admin listener, leave all proxy-header flags `false`.

---

## 5. Buckets

A bucket is a named namespace with its own limits. Buckets are created and configured
**only** through the admin UI — the public API can never create one.

### 5.1 Bucket name rules

- Character set: base32 (`A–Z`, `2–7`), **case-insensitive**.
- Normalize to lowercase on both configuration and request handling. `Photos` and
  `PHOTOS` are the same bucket.
- Length 1–32 characters.
- Optional on every request. If absent or empty, the **default bucket** is used.
- The default bucket's name is configurable, always exists, and cannot be deleted.
- An unrecognized bucket name returns `404 unknown_bucket`. **Never auto-create** —
  auto-creation is an abuse vector and a memory-growth vector.

### 5.2 Per-bucket settings

| Setting              | Meaning                                        | Global default |
|----------------------|------------------------------------------------|----------------|
| `Name`               | base32, lowercase-normalized                   | `default`      |
| `Enabled`            | if false, all ops return `503 bucket_disabled`  | `true`         |
| `DefaultTtlSeconds`  | applied when the client omits `ttl`            | `3600` (1 h)   |
| `MaxTtlSeconds`      | ceiling; client requests above this are clamped| `7200` (2 h)   |
| `MaxItemSizeBytes`   | per-item ceiling                               | `5242880` (5 MiB) |
| `MaxItemCount`       | rows in this bucket before eviction            | `100000`       |
| `MaxTotalBytes`      | bytes in this bucket before eviction           | `1073741824` (1 GiB) |
| `Notes`              | free-text, admin-only                          | `""`           |

- Clients may request a **shorter** TTL than `DefaultTtlSeconds` (minimum 60 s).
  Requests above `MaxTtlSeconds` are clamped, and the response reports the applied
  `expires` so the client can see what it actually got.
- The "global default" column is a template applied when a new bucket is created in the
  admin UI. Changing a global default does not retroactively alter existing buckets.
- Keys are scoped per bucket: the same key string in two buckets is two distinct items.
  The storage identity is `(bucket, key)`.

---

## 6. Key format

Keys are **client-supplied**, but format-constrained.

- **Strict mode (default):** exactly 32 characters matching `^[a-z2-7]{32}$` (RFC 4648
  base32, lowercase, unpadded — 160 bits). Accept mixed case on input and normalize
  down, consistent with bucket names.
- **Permissive mode (config flag, off by default):** `^[A-Za-z0-9_.-]{20,128}$`.

Rationale: unconstrained keys mean users end up with `settings`, `test`, or `1`, and
their data becomes trivially discoverable by dictionary probing — silently, with no
error anyone would notice. A format constraint makes low-entropy keys structurally
impossible without requiring server-side key minting.

BPUtil has no base32 helper; implement `Base32.Encode(byte[])` / `TryDecode(string)`
with RFC 4648 lowercase, unpadded, and reuse it for both keys and bucket names.

---

## 7. Storage design

### 7.1 Split metadata from values

**Values are files on disk. LiteDB stores only metadata.**

```
<data>/blobs/<bucket>/<k0><k1>/<key>.bin      # k0,k1 = first two chars of key
<data>/kvstore.db                             # LiteDB, metadata only
```

Rationale, given a 5 MiB per-item ceiling and a 1 GB RAM budget:

- LiteDB stores documents in 8 KiB pages and materializes a whole `BsonDocument` in
  memory to read or write it. A 5 MiB document is ~640 pages and a 5 MiB allocation per
  concurrent operation. That is LiteDB's worst path and would dominate the memory budget
  under even light concurrency.
- Metadata documents are ~150 bytes, which is LiteDB's strongest path.
- Deleting a file returns space to the filesystem immediately. LiteDB reuses freed pages
  but does not shrink the file without a full `Rebuild()`, so keeping multi-MB blobs out
  of it avoids a compaction problem entirely.
- If the database is lost, only the index is lost; the orphan sweeper (§7.4) reclaims
  the files.

### 7.2 Metadata document

```csharp
public class KvMeta
{
    public string Id { get; set; }        // "<bucket>:<key>" — _id
    public string Bucket { get; set; }
    public long   Size { get; set; }      // bytes on disk
    public long   Created { get; set; }   // unix seconds
    public long   Expires { get; set; }   // unix seconds
}
```

Indexes: `_id` (implicit), `Expires`, `Bucket`, `Created`.

**No source IP field.** See §11.

### 7.3 LiteDB usage rules

- One shared `LiteDatabase` instance for process lifetime. Do not open per request.
- Open with `Connection=Direct` (single process, no shared-mode file locking). Shared
  mode is where most reported LiteDB corruption originates.
- All access through a single repository class; no `LiteDatabase` references elsewhere.
- Run `Checkpoint()` on the maintenance timer to flush the log file.

### 7.4 Corruption is a normal event, not a crisis

Treat database loss as an expected operational condition rather than something to be
prevented. This service's durability requirement is near zero: the maximum item age is
2 hours, and every stored item is a copy of data that still exists on the source device.
Total loss means some users re-export their settings.

On startup, and on any `LiteException` indicating structural damage:

1. Log at error level.
2. Close the database; rename the file to `kvstore.db.corrupt.<timestamp>`.
3. Create a fresh database.
4. Delete the entire `blobs/` tree (all metadata for it is gone).
5. Increment a persistent `RecoveryEventCount` counter and record the timestamp.
6. **Keep serving.** Do not exit.

Surface `RecoveryEventCount` and the last recovery timestamp prominently on the admin
dashboard. Retain at most the 3 most recent `.corrupt` files, then delete oldest.

This is the same failure mode the operator has experienced with SQLite in other
projects. Here it is acceptable — the requirement is that it be *visible and
self-healing* rather than silent.

**Orphan sweeper** (maintenance timer, every 10 minutes): walk `blobs/`, delete any file
with no corresponding metadata document and an mtime older than 10 minutes (the age
check avoids racing an in-flight upload). Also delete metadata whose file is missing.

### 7.5 Write path (crash-safe ordering)

1. Validate bucket, key, size, TTL.
2. Write value to `<final>.tmp`, `FileStream.Flush(true)`, close.
3. `File.Move(tmp, final, overwrite: true)`.
4. Upsert metadata document.

Crash between 3 and 4 leaves an orphan file, which the sweeper reclaims. Crash between
2 and 3 leaves a `.tmp` file; the sweeper deletes `.tmp` files older than 10 minutes.
Never the reverse order — metadata pointing at a missing file would surface as a 500.

---

## 8. HTTP API

All endpoints are `POST` with a JSON body and a JSON response. Base path `/v1`.
Implemented in an `HttpServer` subclass overriding `handlePOSTRequest`.

### 8.1 CORS-safelisted content type

Clients should send `Content-Type: text/plain;charset=UTF-8` with a JSON body. This is a
CORS-safelisted value, so browsers skip the preflight round-trip. The server parses the
body as JSON regardless of declared content type. Also accept `application/json` for
non-browser clients (which will incur a preflight).

### 8.2 `POST /v1/put`

```json
{ "bucket": "default", "key": "<32-char base32>", "value": "<base64>", "ttl": 3600 }
```

`200` → `{ "ok": true, "expires": 1754332800, "ttl": 3600, "size": 4096 }`

- `bucket` optional. `ttl` optional (bucket default), clamped to `[60, MaxTtlSeconds]`;
  the applied value is always echoed back.
- Overwrites unconditionally if `(bucket, key)` exists.
- Reject before reading the body if `Content-Length` exceeds `MaxItemSizeBytes × 1.4`
  (base64 overhead) — return `413` without consuming the request body.
- Stream the body to the temp file rather than buffering the whole base64 string in
  memory; decode incrementally. At 5 MiB per item this matters.

### 8.3 `POST /v1/get`

```json
{ "bucket": "default", "key": "<32-char base32>" }
```

`200` → `{ "ok": true, "value": "<base64>", "expires": 1754332800, "size": 4096 }`
`404` → `{ "ok": false, "error": "not_found" }`

Expired-but-unswept items must be treated as absent — filter on `Expires > now` in the
query rather than relying on the sweeper having run. Reads never extend lifetime.

### 8.4 `POST /v1/del`

`200` → `{ "ok": true, "deleted": true }` (`false` if absent).

### 8.5 `POST /v1/info`

Same request shape as `/get`. Returns `{ ok, exists, expires, size }` without
transferring the value. Lets device B poll for device A's upload cheaply.

### 8.6 `POST /v1/buckets`

`200` → public, read-only bucket metadata so clients can self-configure:

```json
{ "ok": true, "buckets": [
  { "name": "default", "maxItemSizeBytes": 5242880, "defaultTtl": 3600, "maxTtl": 7200 }
] }
```

Only enabled buckets. No counts, no usage, no notes.

### 8.7 `POST /v1/phrase` *(optional helper, stateless)*

```json
{ "words": 6 }
```

`200` → `{ "ok": true, "phrase": "crop-visor-quilt-nomad-brave-flint" }`

EFF short wordlist #1 (1296 words), CSPRNG. Default 6, min 5, max 10. **Stateless** —
nothing is recorded or reserved. Clients that generate locally are preferred; this
exists only so clients don't have to bundle a wordlist.

### 8.8 `GET /v1/health`

`{ "ok": true, "version": "...", "uptime": 12345 }`. No sensitive data. The only GET.

### 8.9 Errors

Uniform JSON, never a stack trace:

| HTTP | `error`             | Cause                                    |
|------|---------------------|------------------------------------------|
| 400  | `bad_request`       | Malformed JSON, missing field            |
| 400  | `invalid_key`       | Key fails format validation              |
| 400  | `invalid_bucket`    | Bucket name fails format validation      |
| 400  | `invalid_value`     | Bad base64                               |
| 404  | `not_found`         | Key absent or expired                    |
| 404  | `unknown_bucket`    | Bucket not configured                    |
| 413  | `too_large`         | Value exceeds bucket's `MaxItemSizeBytes`|
| 429  | `rate_limited`      | Include `Retry-After`                    |
| 503  | `bucket_disabled`   | Bucket disabled by admin                 |
| 503  | `storage_full`      | Hard quota reached and eviction failed   |

---

## 9. CORS

Different origins is the entire point — UI3 is self-hosted at arbitrary origins,
ShoppingList runs from `bp2008.github.io` or from user forks.

```
Access-Control-Allow-Origin:  *
Access-Control-Allow-Methods: POST, OPTIONS
Access-Control-Allow-Headers: Content-Type
Access-Control-Max-Age:       86400
Cache-Control:                no-store
```

- **No credentials.** `Access-Control-Allow-Credentials` must never be set — it is
  incompatible with `*`, and there are no cookies on the public API anyway.
- Handle `OPTIONS` on all `/v1/*` paths (override `handleOPTIONSRequest` or intercept in
  the request router), return `204` immediately — before rate limiting, before body
  parsing.

---

## 10. Rate limiting — `TokenBucketDictionary`

In-memory only. Restart resets all buckets; that is acceptable.

### 10.1 Instances

Three `TokenBucketDictionary<string>` instances keyed on the normalized client IP:

| Dictionary | Capacity | RefillRate | Consumed by            |
|------------|----------|------------|------------------------|
| Writes     | 10       | 0.0167/s (60/hr) | `put`, `del`     |
| Reads      | 30       | 0.167/s (600/hr) | `get`, `info`    |
| Bytes      | 10485760 | 121.4/s (~10 MiB/day) | `put`, consume `size` |

`TryConsume` takes a `double`, so the bytes dictionary consumes the item size directly.

### 10.2 Key normalization

- IPv4: the address string.
- **IPv6: truncate to the /64 prefix.** A single /64 is routinely assigned to one
  customer, so per-address limiting is trivially bypassed.

### 10.3 Runtime reconfiguration

`Capacity` and `RefillRate` have `private set` and are fixed at construction. To apply an
admin config change, **construct a replacement dictionary and swap the reference**
(`Volatile.Write` or a lock). Do not attempt to mutate in place. Document that a limit
change resets everyone's buckets to full.

### 10.4 Memory guard

`TokenBucketDictionary` only removes buckets once they refill to capacity, so a
distributed flood can grow the dictionary without bound. Expose `NumberOfBuckets` on the
dashboard, and add a configurable ceiling (default 250,000): above it, reject new keys
with `429` rather than allocating. Set `maintenanceIntervalMilliseconds` to 30000.

### 10.5 Outer layer

Configure Cloudflare's free-tier rate limiting rules so the bulk of abusive traffic never
reaches the origin at all.

---

## 11. Logging — no IP addresses

**Client IP addresses are never written to disk.** Not to an access log, not to the
metadata database, not to an error log.

- IPs exist only as ephemeral keys inside the `TokenBucketDictionary` instances.
- `HttpServer.shouldLogRequestsToFile()` must return `false` on the public listener.
  BPUtil's `SimpleHttpLogger.LogRequest` writes `RemoteIPAddressStr`; it must not be
  enabled here.
- Error logs must scrub IPs. Audit any BPUtil error path that might include a remote
  endpoint before logging.
- Aggregate counters only: total requests, per-endpoint counts, error counts, 429 counts,
  bytes in/out. No per-client dimension.

**Consequence, accepted deliberately:** without IP records the operator cannot answer a
subpoena about a specific item, cannot retroactively identify an abuser, and cannot ban
a specific offender after the fact. The declared fallback is to restrict or shut off the
service. This is a reasonable trade for a service holding 2-hour-lifetime opaque blobs,
and it also eliminates an entire category of breach liability. Revisit only if abuse
actually occurs.

---

## 12. Admin interface

Separate `HttpServer` listener on its own port, following the WebProxy pattern.

### 12.1 Authentication — HTTP Digest

Use the existing BPUtil methods:

- `p.GetDigestAuthWWWAuthenticateHeaderValue(realm)` on the `401` challenge.
- `p.ValidateDigestAuth(realm, validCredentials)` to check the `Authorization` header.

**Constraints this imposes, and they are unavoidable:**

- `ValidateDigestAuth` takes `IEnumerable<NetworkCredential>`, i.e. the **plaintext
  password**. Digest requires the server to know the password (or HA1). It therefore
  **cannot** be stored as a one-way hash. Store it in the settings file, `chmod 0600`,
  owned by the service user, outside the published binary directory.
- BPUtil's implementation uses MD5 and, per its own XML docs, **does not guard against
  replay attacks**. The admin listener must therefore be HTTPS-only and reachable only
  over VPN/loopback. Both are already required by §3.
- Digest re-authenticates on every request by design, so **no action ever prompts for
  the password again**. There is no session, no cookie, no re-entry. This satisfies the
  stated requirement directly.
- *Optional BPUtil enhancement (not required):* add an overload accepting a precomputed
  HA1 = `MD5(user:realm:password)` so the settings file holds HA1 instead of the
  password. This is a marginal improvement — HA1 is password-equivalent for
  authentication — but it prevents password reuse leakage if the file is exposed.

Rate-limit failed authentication attempts (5/min, then exponential backoff) using a
fourth `TokenBucketDictionary`.

### 12.2 What the admin UI must NOT show

Non-negotiable, and directly load-bearing for §14:

- **No record keys.** Not partial, not hashed, not searchable.
- **No record values.** No reveal action, no hex dump, no export. There is no code path
  from the admin UI to a stored value.
- **No client IP addresses.** They do not exist on disk to display.

The admin UI is a health and configuration console, not a data browser.

### 12.3 Dashboard — statistics

Global and per-bucket:

- Item count; total bytes; percentage of quota consumed.
- Item size: **average, minimum, maximum**, plus a histogram (0–1 KiB, 1–16 KiB,
  16–256 KiB, 256 KiB–1 MiB, 1–5 MiB).
- Age distribution: items expiring in <5 min, <30 min, <1 h, >1 h.
- Ops counters over 1 min / 1 h / 24 h: puts, gets, dels, infos, 404s, 413s, 429s, 5xxs.
- Bytes in / bytes out over the same windows.
- Eviction counters: expired-swept, quota-evicted, orphans reclaimed.
- `RecoveryEventCount` and last recovery timestamp (§7.4) — visually prominent.
- `NumberOfBuckets` for each rate limiter; ceiling utilization.
- Process: uptime, managed heap size, GC gen2 count, thread count, disk free on the
  data volume, LiteDB file size, blob tree size.

Compute size statistics incrementally from write/delete events where possible; a full
`SUM`/`AVG` scan over LiteDB on every dashboard load is acceptable at expected scale but
should be cached for ~10 seconds.

### 12.4 Configuration screens

- **Buckets:** create, rename, enable/disable, delete (deletes all items), and edit every
  field in §5.2. Deleting or disabling the default bucket is blocked.
- **Global defaults:** the template values applied to newly created buckets.
- **Rate limits:** capacity and refill rate for each of the three dictionaries, plus the
  bucket-count ceiling. Applying a change swaps the instances (§10.3) and must warn that
  all current buckets reset to full.
- **Key format:** strict / permissive toggle.
- **Maintenance:** intervals, orphan sweep age threshold.
- **Danger zone:** flush a bucket; flush everything; force a LiteDB `Rebuild()`.

Settings persist via `BPUtil.SerializableObjectBase` (`Save()` / `Load()` /
`SaveIfNoExist()`), consistent with WebProxy. Changes take effect without restart.

---

## 13. Client-side encryption (convention, not enforced)

The server cannot enforce this and must not try. Document it as the recommended
convention for client apps and implement it in the reference JS client.

Derive **one** secret phrase into **two** keys — this costs zero additional phrase
entropy, which is the whole point:

```
phrase     = 6 words from EFF short wordlist #1          (~62 bits)
material   = PBKDF2-SHA256(phrase, salt="bp2008-kv-v1", iterations=600000, dkLen=64)
lookupKey  = base32(material[0..20])   -> 32 chars, sent to the server
contentKey = material[32..64]          -> AES-256-GCM key, never transmitted
```

- AES-256-GCM, random 12-byte IV prepended to the ciphertext.
- PBKDF2 rather than Argon2id because it is native to WebCrypto and needs no WASM
  dependency. Use Argon2id where a WASM dependency is acceptable.
- Six words (~62 bits) is the recommended minimum. Four words (~41 bits) is adequate
  against online guessing given §10 but is within reach of an offline attack on a stolen
  database.

What this buys and doesn't:

- **Does:** protect stored data against anyone with disk access who lacks the phrase.
- **Does not:** add any resistance to phrase-guessing. Guessing the phrase yields both
  keys. Brute-force resistance comes from phrase length and rate limiting alone.

---

## 14. Legal and abuse posture

*Not legal advice. The operator should get a real opinion before launch.*

Operator tasks, which the code must support:

1. **Register a DMCA designated agent** with the U.S. Copyright Office
   (`copyright.gov/dmca-directory`). ~$6, and it **expires after three years** — a lapse
   in registration is a lapse in safe-harbor protection. The Office emails reminders at
   90/60/30/7 days; set an independent calendar reminder too.
2. **Register with NCMEC as an electronic service provider** before it is needed. Under
   18 U.S.C. § 2258A there is a duty to report to NCMEC as soon as reasonably possible
   after obtaining **actual knowledge**, with fines up to $150,000 for an initial knowing
   and willful failure and $300,000 for subsequent ones. The statute expressly does
   **not** require providers to monitor users or affirmatively scan content.
3. **Publish a Terms of Service and an abuse contact** (`abuse@`) reachable from the
   landing page, with a documented takedown workflow. Because there is no key
   enumeration, a takedown request must supply the exact bucket and key; the operator
   deletes it via the danger-zone flush or a targeted CLI command.

Design decisions that follow, which must not be reverted:

- **No inline serving, no GET retrieval, no content types.** This is what keeps the
  service from being usable as an image or file host, which is where essentially all
  liability exposure lives.
- **Short TTLs.** A 2-hour ceiling makes the service near-worthless as a distribution
  channel. Abuse concentrates on services with persistent links.
- **No enumeration, anywhere.** Nothing can be browsed or indexed, including by the
  operator.
- **The admin UI cannot display values.** Routinely viewing user content would convert a
  "no duty to monitor" posture into potential actual knowledge. The absence of a reveal
  path keeps that boundary clean and is why §12.2 is non-negotiable.
- Serve `X-Robots-Tag: noindex, nofollow` and a blanket-disallow `robots.txt` on the
  landing page.

---

## 15. Hardening checklist

- [ ] Public listener: `shouldLogRequestsToFile()` returns `false`.
- [ ] Public listener: only `CFConnectingIPHeader` enabled; `IsTrustedProxyServer` returns
      `true` for loopback only.
- [ ] Admin listener: all proxy-header flags `false`; HTTPS only; bound to VPN/loopback.
- [ ] Request body size ceiling enforced before reading the body.
- [ ] No exception details in responses; generic `500` with a correlation ID logged.
- [ ] Settings file mode `0600`, owned by the service user.
- [ ] Data directory outside the published binary directory (`Globals.WritableDirectoryBase`).
- [ ] systemd hardening added to the generated unit: `NoNewPrivileges=yes`,
      `PrivateTmp=yes`, `ProtectSystem=strict`, `ProtectHome=yes`,
      `ReadWritePaths=<data dir>`, `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`.
- [ ] Convert the Oracle account to Pay As You Go — Always Free compute is reclaimed when
      95th-percentile CPU, network, *and* (on A1 shapes) memory all stay under 20% for
      7 days. PAYG exempts the instance and costs nothing within Always Free limits.

---

## 16. Acceptance tests

1. `put` then `get` round-trips a 5 MiB binary payload byte-for-byte.
2. A 5 MiB + 1 byte payload returns `413` and the request body is not fully buffered.
3. `get` on an expired-but-unswept item returns `404`.
4. Keys `settings`, `test`, `""`, a 200-char key are rejected `invalid_key` in strict
   mode; a 32-char uppercase key is accepted and normalized.
5. Bucket names `Photos` / `PHOTOS` / `photos` resolve to the same bucket; `photo!` is
   rejected; `nonexistent` returns `unknown_bucket` and does **not** create a bucket.
6. Same key in two buckets stores two independent values.
7. A `ttl` above the bucket's `MaxTtlSeconds` is clamped and the response reports the
   clamped value; a `ttl` below 60 is raised to 60.
8. A browser `fetch` from `https://bp2008.github.io` succeeds with no preflight using
   `Content-Type: text/plain`, and with preflight using `application/json`.
9. Exceeding the write limit returns `429` with a valid `Retry-After`.
10. Two addresses in the same IPv6 /64 share a rate-limit bucket.
11. Changing a rate limit in the admin UI swaps the dictionary and takes effect on the
    next request without restart.
12. `CF-Connecting-IP` takes precedence over `X-Real-Ip` and `X-Forwarded-For` when all
    three are present.
13. A forged `CF-Connecting-IP` on a connection from a non-trusted peer is ignored.
14. Grep of all log files after a load test contains zero IP addresses.
15. Corrupting `kvstore.db` on disk and restarting: service recovers, recreates the
    database, clears `blobs/`, increments `RecoveryEventCount`, keeps serving, and does
    not exit.
16. Killing the process between blob write and metadata upsert leaves an orphan that the
    sweeper reclaims within one cycle.
17. Sustained put/delete churn for 100k operations leaves the blob tree size flat and the
    LiteDB file at a stable steady state.
18. Admin UI has no route, parameter, or API that returns a key, a value, or an IP.
19. Admin UI is unreachable from the public interface; the public API is unreachable on
    the admin port.
20. Peak managed heap stays under 400 MB during 20 concurrent 5 MiB uploads.
