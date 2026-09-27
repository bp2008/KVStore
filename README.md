# KVStore

A light-weight key-value-store server written in C# and supporting anonymous user access and web-based configuration.

## Purpose

This key-value store server was designed to provide simple applications with a way to store and retrieve temporary data in the cloud.  The specific goal I had in mind was to facilitate export and import features: One device uploads data to the key-value store and displays a QR code containing key information.  Another device scans the QR code and retrieves the data from the key-value store.  Setup is simple for the developer, and even simpler for the user.  No account setup is required, no peer-to-peer connection.  Just an internet connection and basic I/O capabilities (QR codes are an optional convenience; the key-value-store doesn't care how you share the key information).

**The security model is the key itself.**  Anyone who knows a key can read, overwrite, or delete its value.  This is intentional; there are no accounts, tokens, or API keys, because self-hosted and serverless web apps can't keep a secret API key anyway.

Specific design decisions were made to facilitate this purpose and reduce the risk of abuse:
* Keys are chosen by the client but must be high-entropy: by default exactly 32 base32 characters (160 bits).  Low-entropy keys like `settings` or `test` are rejected, so data can't be found by guessing.
* Clients are encouraged to encrypt data before sending it to the server (see [Client-side encryption](#client-side-encryption)).
* Objects uploaded by clients are automatically deleted after an admin-configurable amount of time (intended to be minutes or hours; 2 hours maximum by default).
* No `GET /{key}` retrieval.  **All KV operations are POST.**  This prevents the service from being used as a link-shareable file host.
* No `Content-Type` awareness for stored values.  Values are treated as opaque byte arrays.
* No listing, enumeration, or search of stored keys, including in the admin console.  The admin console has no way to view stored values.
* No logging of client IP addresses or other identifying information.  IP addresses exist only in memory, as keys for rate limiting.

## Deployment

KVStore is designed to run on a small Linux VM (1 GB RAM is plenty) behind [Cloudflare](https://www.cloudflare.com/):

```
Internet ──> Cloudflare (proxied DNS) ──> Cloudflare Tunnel ──> 127.0.0.1:8080  Public API
                                                                127.0.0.1:8081  Admin console (NOT tunneled)
```

* The **public API** listens on `127.0.0.1:8080` (http) and is exposed to the internet only through `cloudflared`.  Client IP addresses are taken from the `CF-Connecting-IP` header, which is trusted only on connections from loopback.
* The **admin console** listens on `127.0.0.1:8081` (https, self-signed certificate) and must never be exposed to the internet.  Reach it through an SSH tunnel (`ssh -L 8081:127.0.0.1:8081 you@server`) or bind it to a VPN interface address in `Settings.json`.
* Because Cloudflare Tunnel is outbound-only, the server needs no inbound ports besides SSH (and your VPN, if any).  Configure the cloud provider's firewall and the host firewall to deny everything else.
* Configure Cloudflare's free rate limiting rules as an outer layer, so most abusive traffic never reaches the server.
* On Oracle Cloud Always Free, convert the account to Pay As You Go.  Always Free instances can be reclaimed when CPU, network, and memory use all stay low for a week, which describes this service.

### Linux installation script

On Ubuntu 22.04+ (and other distributions the script supports), run:

```bash
rm -f kvstore_install.sh
wget https://raw.githubusercontent.com/bp2008/KVStore/main/KVStore/kvstore_install.sh
chmod u+x kvstore_install.sh
./kvstore_install.sh
```

The script is interactive.  It installs the .NET 10.0 runtime if needed, downloads a release into `~/kvstore`, installs and starts the `kvstore` systemd service, and optionally installs `cloudflared`.  Installing the service also writes a systemd drop-in (`/etc/systemd/system/kvstore.service.d/hardening.conf`) that sandboxes the service (`NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict`, `ProtectHome`, `ReadWritePaths` limited to the data directory, and restricted address families).

Settings, logs, the database, and stored values live in `/usr/share/KVStoreLinux/`.  The settings file is kept at mode `0600` because it contains the admin console password.

Run `sudo /usr/bin/dotnet ~/kvstore/KVStoreLinux.dll` for the command-line menu, which can show the admin console credentials and URLs (`admin`), manage the service, and delete a specific item in response to an abuse report (`takedown`).

### Windows

The Windows build (`KVStore.exe`) opens a service manager window with Install/Start/Stop buttons and an "Admin Console" button that shows the admin console URL and credentials.

## Public API

All endpoints are under `/v1/`.  Key/value operations are `POST` requests with a JSON body and a JSON response, except `putraw` and `getraw`, which carry the value as raw bytes.  Browsers should send `Content-Type: text/plain;charset=UTF-8`, which is CORS-safelisted, so no preflight request is needed; the body is parsed as JSON regardless of the declared content type.  CORS allows any origin, without credentials.

The API documentation page (`/api`) describes every operation's fields in detail.  The public site also has a browser-based API tester (`/api-tester`) and a self-service takedown form (`/takedown`).

| Endpoint | Request body | Success response |
|---|---|---|
| `POST /v1/put` | `{ "bucket": "default", "key": "<key>", "value": "<base64>", "ttl": 3600 }` | `{ "ok": true, "expires": 1754332800, "ttl": 3600, "size": 4096 }` |
| `POST /v1/get` | `{ "bucket": "default", "key": "<key>" }` | `{ "ok": true, "value": "<base64>", "expires": 1754332800, "size": 4096 }` |
| `POST /v1/putraw?key=<key>&bucket=default&ttl=3600` | The value's raw bytes | Same as `put` |
| `POST /v1/getraw` | `{ "bucket": "default", "key": "<key>" }` | The value's raw bytes (`application/octet-stream`), with `KV-Expires` and `KV-Size` headers |
| `POST /v1/info` | `{ "bucket": "default", "key": "<key>" }` | `{ "ok": true, "exists": true, "expires": 1754332800, "size": 4096 }` |
| `POST /v1/del` | `{ "bucket": "default", "key": "<key>" }` | `{ "ok": true, "deleted": true }` |
| `POST /v1/buckets` | `{}` | `{ "ok": true, "defaultBucket": "default", "buckets": [ { "name": "default", "maxItemSizeBytes": 5242880, "defaultTtl": 3600, "maxTtl": 7200 } ] }` |
| `POST /v1/phrase` | `{ "words": 6 }` | `{ "ok": true, "phrase": "crop-visor-quilt-nomad-brave-flint" }` |
| `GET /v1/health` | | `{ "ok": true, "version": "1.0.0.0", "uptime": 12345 }` |

* **Keys** are 32 characters from the RFC 4648 base32 alphabet (`a-z`, `2-7`), case-insensitive.  An administrator can enable a permissive mode that also accepts `^[A-Za-z0-9_.-]{20,128}$` (case-sensitive).
* **Buckets** are namespaces with their own limits, created only by the administrator.  `bucket` is optional; if omitted, the default bucket is used.  Bucket names are 1-32 base32 characters, case-insensitive.
* **TTL** is optional.  It is clamped to the range [60 seconds, the bucket's maximum], and the response reports the TTL that was applied.  Reading an item never extends its lifetime.
* `putraw` and `getraw` avoid base64's 33% overhead.  `putraw` takes `key`, `bucket`, and `ttl` as URL parameters rather than headers, because custom headers would require a CORS preflight request; browsers should send the body with no `Content-Type` (the default for a `Uint8Array`) or `text/plain`.  A failed `getraw` returns the usual JSON error, so check the status before treating the body as the value.
* `put` overwrites unconditionally.  `info` lets a device poll for another device's upload without downloading it.
* `phrase` returns random words from the [EFF short wordlist #1](https://www.eff.org/dice).  Nothing is recorded.  Generating phrases on the client is preferred.

Errors are reported as `{ "ok": false, "error": "<code>" }`:

| HTTP | `error` | Cause |
|---|---|---|
| 400 | `bad_request` | Malformed JSON, or a missing field or parameter |
| 400 | `invalid_key` | Key fails format validation |
| 400 | `invalid_bucket` | Bucket name fails format validation |
| 400 | `invalid_value` | Bad base64 |
| 404 | `not_found` | Key absent or expired |
| 404 | `unknown_bucket` | Bucket not configured |
| 404 | `unknown_endpoint` | No such operation |
| 405 | `method_not_allowed` | Wrong HTTP method |
| 413 | `too_large` | Value exceeds the bucket's maximum item size |
| 429 | `rate_limited` | Too many requests; see the `Retry-After` header |
| 503 | `bucket_disabled` | Bucket disabled by the administrator |
| 503 | `storage_full` | Bucket quota reached and eviction could not make room, or the disk is nearly full |
| 500 | `internal_error` | Unexpected error; the response includes a `correlationId` that appears in the server's error log |

Requests are rate-limited per client (IPv4 address, or IPv6 /64 prefix).  By default: 10 writes (`put`, `putraw`, `del`) refilled at 60/hour, 30 reads (`get`, `getraw`, `info`) refilled at 600/hour, and 10 MiB of stored data refilled at 60 MiB/hour (a full refill takes 10 minutes).

## Client-side encryption

The server can't enforce encryption and doesn't try.  The recommended convention, implemented by the reference client [`Client/kvstore-client.js`](Client/kvstore-client.js), derives **one** secret phrase into **two** keys, which costs no additional phrase entropy:

```
phrase     = 6 words from the EFF short wordlist #1          (~62 bits)
material   = PBKDF2-SHA256(phrase, salt="bp2008-kv-v1", iterations=600000, dkLen=64)    (10000 iterations in "fast" mode)
lookupKey  = base32(material[0..20])   -> 32 chars, sent to the server
contentKey = material[32..64]          -> AES-256-GCM key, never transmitted
```

The stored value is AES-256-GCM ciphertext with a random 12-byte IV prepended.  Phrases are normalized before derivation: lower case, words separated by single hyphens.

```js
const kv = new KVStoreClient("https://kv.example.com");
const phrase = await kv.phrase(6);                     // show this to the user (or as a QR code)
await kv.putEncrypted(phrase, JSON.stringify(settings)); // device A
const json = await kv.getEncryptedText(phrase);        // device B
```

**Fast mode.**  `new KVStoreClient(url, { keyDerivation: "fast" })` uses 10,000 PBKDF2 iterations instead of 600,000, so key derivation is 60 times faster, which matters most for the legacy client in old browsers.  Each guess also becomes 60 times cheaper for anyone attacking a phrase offline, such as someone with a copy of the server's disk testing guesses against the lookup keys.  For a randomly generated phrase of 6 or more words (about 62 bits or more), that is still impractical, so use fast mode only with generated phrases of at least 6 words, never with phrases people choose themselves.  The two modes derive different keys, so every device that shares a phrase must use the same mode.

For pages served over plain http (where browsers withhold the WebCrypto API that the reference client needs) or old browsers down to Internet Explorer 9, use [`Client/kvstore-client-legacy.js`](Client/kvstore-client-legacy.js) instead.  It implements the same convention in dependency-free ES5 with callbacks, so either client can read what the other stores, but its pure-JavaScript key derivation is several times slower than native, and in IE8/9 cross-origin requests go through `XDomainRequest`, which hides server error details and requires the page and the API to use the same scheme.

Encryption protects stored data from anyone with access to the server's disk who lacks the phrase.  It adds no resistance to guessing the phrase: brute-force resistance comes from the phrase length and the rate limits.  Six words is the recommended minimum.

## Admin Console

The admin console uses HTTP Digest authentication.  Digest authentication requires the server to know the password, so it is stored in plain text in `Settings.json` (mode `0600`).  Because BPUtil's Digest implementation does not prevent replay attacks, the admin console is HTTPS-only by default and must only be reachable over loopback or a VPN.  Failed logins are limited to 5 per minute per client, followed by exponential backoff.

* **Dashboard:** item counts, bytes and quota use per bucket; item size statistics and histograms; time-until-expiration distribution; operation counts over 1 minute / 1 hour / 24 hours; eviction and orphan counters; rate limiter memory use; process statistics.  Database recovery events are shown prominently.
* **Buckets:** create, edit, rename, enable/disable, delete, and choose the default bucket.
* **Settings:** global defaults for new buckets, rate limits, key format, maintenance intervals, and the public listener and landing page details.
* **Danger zone:** delete a single item by bucket and key (for takedown requests), flush a bucket, flush everything, rebuild the database.

The admin console can't display keys, values, or IP addresses, by design.  All configuration changes take effect without a restart.  Changing rate limits resets every client's rate limit buckets to full.

## Storage and reliability

Values are stored as files (`data/blobs/<bucket>/<k0><k1>/<key>.bin`) and only metadata is stored in a [LiteDB](https://www.litedb.org/) database (`data/kvstore.db`).  Uploads are streamed to disk and downloads are streamed from disk, so memory use stays low even with many concurrent multi-megabyte transfers.

Database damage is treated as a normal event rather than a crisis: every stored item is short-lived and is a copy of data that still exists on the uploading device.  If the database is found damaged, it is renamed to `kvstore.db.corrupt.<timestamp>` (the 3 most recent are kept), a fresh database is created, all stored values are deleted, the event is counted on the dashboard, and the service keeps running.  A background sweep deletes expired items, and an orphan sweep reclaims files left behind by crashes.

## Operator responsibilities

*This is not legal advice.  Get a real opinion before launching a public service.*

1. Register a [DMCA designated agent](https://www.copyright.gov/dmca-directory/) with the U.S. Copyright Office.  Registration expires after three years.
2. Register with NCMEC as an electronic service provider before you need to.  Providers must report apparent child sexual abuse material to NCMEC after obtaining actual knowledge of it (18 U.S.C. § 2258A), but are not required to monitor or scan content.
3. Set `operatorName` in the admin console.  It is shown on the landing page (`/`) together with a Terms of Service template, which you should review.  No contact information is published: anyone who has an item's bucket and key can delete it immediately with the self-service takedown form (`/takedown`), which uses the public `del` operation.  If a takedown request reaches you another way, delete the item with the admin console's danger zone or the `takedown` command.

## Building

Requires the .NET 10 SDK and a checkout of [BPUtil](https://github.com/bp2008/BPUtil) next to this repository (`../BPUtil`).  `KVStore.Tests` contains unit and integration tests (`dotnet test KVStore.Tests`).

### Making a release

1. Increase `<Version>` in `KVStore/KVStore.csproj`, then build the solution in Visual Studio using the **Release** configuration.
2. Run `ReleaseArchiver/bin/ReleaseArchiver.exe`.  It creates `KVStore Linux <version>.zip` and `KVStore Windows <version>.zip` in the `Releases` folder.  It refuses to package a build that is missing, incomplete, or older than the source files.
3. Create a GitHub release and attach both zip files.  The Linux installation script installs the release asset whose name contains `Linux`.

## Acknowledgements

* `KVStore/Resources/eff_short_wordlist_1.txt` is the EFF Short Wordlist #1 by the [Electronic Frontier Foundation](https://www.eff.org/dice), licensed under [CC BY 3.0 US](https://creativecommons.org/licenses/by/3.0/us/).
* Metadata storage uses [LiteDB](https://github.com/litedb-org/LiteDB) (MIT license).
