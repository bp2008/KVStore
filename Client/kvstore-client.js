/*
 * KVStore reference client.
 *
 * A dependency-free browser client for a KVStore server, implementing the recommended client-side encryption convention:
 * one secret word phrase is derived into two keys, a lookup key (sent to the server) and a content key (never transmitted).
 *
 *   phrase     = 6 words from the EFF short wordlist #1                (~62 bits)
 *   material   = PBKDF2-SHA256(phrase, salt="bp2008-kv-v1", iterations=600000, dkLen=64)    (10000 iterations in "fast" mode)
 *   lookupKey  = base32(material[0..20])  -> 32 chars, sent to the server
 *   contentKey = material[32..64]         -> AES-256-GCM key, never transmitted
 *
 * Stored values are AES-256-GCM ciphertext with a random 12-byte IV prepended.
 *
 * Usage:
 *   const kv = new KVStoreClient("https://kv.example.com");
 *   const phrase = (await kv.phrase(6));                // or KVStoreClient.generatePhrase(wordList, 6)
 *   await kv.putEncrypted(phrase, "some settings JSON"); // device A
 *   const text = await kv.getEncryptedText(phrase);     // device B
 *
 * Key derivation modes.  "standard" (the default) uses 600,000 PBKDF2 iterations.  "fast" uses 10,000, so it is 60 times faster, which
 * matters most for kvstore-client-legacy.js in old browsers.  It is also 60 times cheaper for anyone trying to guess a phrase offline
 * (for example from lookup keys on the server's disk), so use it only with randomly generated phrases of at least 6 words.  The modes
 * derive different keys, so every device must use the same mode: new KVStoreClient(url, { keyDerivation: "fast" }).
 *
 * Requires a secure context (https or localhost) for WebCrypto.
 */
(function (root)
{
	"use strict";

	var SALT = "bp2008-kv-v1";
	var PBKDF2_ITERATIONS = { standard: 600000, fast: 10000 };
	var BASE32_ALPHABET = "abcdefghijklmnopqrstuvwxyz234567";

	/**
	 * Returns the PBKDF2 iteration count for a key derivation mode ("standard" if omitted), or 0 if the mode is unknown.
	 */
	function pbkdf2Iterations(mode)
	{
		mode = mode || "standard";
		return Object.prototype.hasOwnProperty.call(PBKDF2_ITERATIONS, mode) ? PBKDF2_ITERATIONS[mode] : 0;
	}
	function unknownModeMessage(mode)
	{
		return "Unknown key derivation mode \"" + mode + "\".  Use \"standard\" or \"fast\".";
	}

	/**
	 * An error returned by the KVStore server.
	 * @param {number} status HTTP status code.
	 * @param {string} code Error code from the response, e.g. "not_found" or "rate_limited".
	 * @param {number} retryAfter Seconds to wait before retrying (for "rate_limited"), otherwise 0.
	 */
	function KVStoreError(status, code, retryAfter)
	{
		this.name = "KVStoreError";
		this.status = status;
		this.code = code;
		this.retryAfter = retryAfter || 0;
		this.message = "KVStore error " + status + ": " + code + (retryAfter ? " (retry after " + retryAfter + " seconds)" : "");
		this.stack = new Error(this.message).stack;
	}
	KVStoreError.prototype = Object.create(Error.prototype);
	KVStoreError.prototype.constructor = KVStoreError;

	/**
	 * Constructs a client.
	 * @param {string} baseUrl Base URL of the KVStore server, e.g. "https://kv.example.com".
	 * @param {Object} [options]
	 * @param {string} [options.bucket] Bucket to use.  If omitted, the server's default bucket is used.
	 * @param {string} [options.keyDerivation] "standard" (the default, 600,000 PBKDF2 iterations) or "fast" (10,000).  Used by putEncrypted, getEncrypted, and getEncryptedText.
	 */
	function KVStoreClient(baseUrl, options)
	{
		this.baseUrl = String(baseUrl).replace(/\/+$/, "");
		this.bucket = options && options.bucket ? options.bucket : undefined;
		this.keyDerivation = options && options.keyDerivation ? options.keyDerivation : "standard";
		if (!pbkdf2Iterations(this.keyDerivation))
			throw new Error(unknownModeMessage(this.keyDerivation));
	}

	/**
	 * POSTs a JSON body to an API endpoint.  The body is sent as text/plain, which is a CORS-safelisted content type, so browsers do not send a preflight request.
	 */
	KVStoreClient.prototype._post = function (endpoint, body)
	{
		return this._fetch(endpoint, JSON.stringify(body || {}), "text/plain;charset=UTF-8").then(KVStoreClient._readJson);
	};
	/**
	 * POSTs to an API endpoint.  If contentType is null, no Content-Type header is sent (fetch sends none for a Uint8Array body), which also needs no preflight.
	 */
	KVStoreClient.prototype._fetch = function (endpointAndQuery, body, contentType)
	{
		return fetch(this.baseUrl + "/v1/" + endpointAndQuery, {
			method: "POST",
			headers: contentType ? { "Content-Type": contentType } : {},
			body: body,
			credentials: "omit",
			cache: "no-store"
		});
	};
	/**
	 * Reads a JSON response, resolving with it if "ok" is true, otherwise rejecting with a KVStoreError.
	 */
	KVStoreClient._readJson = function (response)
	{
		return response.json().catch(function () { return null; }).then(function (json)
		{
			if (json && json.ok)
				return json;
			var retryAfter = parseInt(response.headers.get("Retry-After"), 10) || 0;
			throw new KVStoreError(response.status, json && json.error ? json.error : "http_" + response.status, retryAfter);
		});
	};
	KVStoreClient.prototype._keyBody = function (key)
	{
		var body = { key: key };
		if (this.bucket)
			body.bucket = this.bucket;
		return body;
	};

	/**
	 * Stores a value without encrypting it.  Overwrites any existing value with the same key.  Uses the "putraw" operation, which sends the bytes as the request body (no base64 overhead).
	 * @param {string} key 32 base32 characters.
	 * @param {Uint8Array} bytes The value.
	 * @param {number} [ttl] Requested lifetime in seconds.  The server clamps it to the bucket's limits.
	 * @returns {Promise<{ok: boolean, expires: number, ttl: number, size: number}>}
	 */
	KVStoreClient.prototype.putRaw = function (key, bytes, ttl)
	{
		var query = "key=" + encodeURIComponent(key);
		if (this.bucket)
			query += "&bucket=" + encodeURIComponent(this.bucket);
		if (ttl)
			query += "&ttl=" + encodeURIComponent(ttl);
		return this._fetch("putraw?" + query, bytes, null).then(KVStoreClient._readJson);
	};
	/**
	 * Retrieves a value stored with putRaw (or by any other client).  Resolves with null if the key does not exist (or has expired).  Uses the "getraw" operation, which returns the bytes as the response body.
	 * @param {string} key
	 * @returns {Promise<Uint8Array|null>}
	 */
	KVStoreClient.prototype.getRaw = function (key)
	{
		return this._fetch("getraw", JSON.stringify(this._keyBody(key)), "text/plain;charset=UTF-8").then(function (response)
		{
			if (response.status === 200)
				return response.arrayBuffer().then(function (ab) { return new Uint8Array(ab); });
			return KVStoreClient._readJson(response);
		}).catch(function (e)
		{
			if (e instanceof KVStoreError && e.code === "not_found")
				return null;
			throw e;
		});
	};
	/**
	 * Returns whether a value exists, without downloading it.  Useful for polling while another device uploads.
	 * @param {string} key
	 * @returns {Promise<{ok: boolean, exists: boolean, expires?: number, size?: number}>}
	 */
	KVStoreClient.prototype.info = function (key)
	{
		return this._post("info", this._keyBody(key));
	};
	/**
	 * Deletes a value.
	 * @param {string} key
	 * @returns {Promise<boolean>} True if a value was deleted.
	 */
	KVStoreClient.prototype.del = function (key)
	{
		return this._post("del", this._keyBody(key)).then(function (r) { return r.deleted; });
	};
	/**
	 * Lists the server's enabled buckets and their limits.
	 * @returns {Promise<{ok: boolean, defaultBucket: string, buckets: Array<{name: string, maxItemSizeBytes: number, defaultTtl: number, maxTtl: number}>}>}
	 */
	KVStoreClient.prototype.buckets = function ()
	{
		return this._post("buckets", {});
	};
	/**
	 * Asks the server for a random phrase.  Generating the phrase locally with generatePhrase() is preferred when you can bundle the word list.
	 * @param {number} [words] 5 to 10 (default 6).
	 * @returns {Promise<string>}
	 */
	KVStoreClient.prototype.phrase = function (words)
	{
		return this._post("phrase", { words: words || 6 }).then(function (r) { return r.phrase; });
	};

	/**
	 * Encrypts and stores data under the lookup key derived from the phrase.
	 * @param {string} phrase The secret phrase.
	 * @param {Uint8Array|string} data Bytes, or a string (encoded as UTF-8).
	 * @param {number} [ttl] Requested lifetime in seconds.
	 */
	KVStoreClient.prototype.putEncrypted = function (phrase, data, ttl)
	{
		var self = this;
		var bytes = typeof data === "string" ? new TextEncoder().encode(data) : data;
		return KVStoreClient.deriveKeys(phrase, this.keyDerivation).then(function (keys)
		{
			return KVStoreClient.encrypt(keys.contentKey, bytes).then(function (ciphertext)
			{
				return self.putRaw(keys.lookupKey, ciphertext, ttl);
			});
		});
	};
	/**
	 * Retrieves and decrypts data stored with putEncrypted.  Resolves with null if nothing is stored under the phrase.  Rejects if decryption fails (wrong phrase or tampered data).
	 * @param {string} phrase The secret phrase.
	 * @returns {Promise<Uint8Array|null>}
	 */
	KVStoreClient.prototype.getEncrypted = function (phrase)
	{
		var self = this;
		return KVStoreClient.deriveKeys(phrase, this.keyDerivation).then(function (keys)
		{
			return self.getRaw(keys.lookupKey).then(function (ciphertext)
			{
				return ciphertext === null ? null : KVStoreClient.decrypt(keys.contentKey, ciphertext);
			});
		});
	};
	/**
	 * Like getEncrypted, but decodes the result as UTF-8 text.
	 * @param {string} phrase The secret phrase.
	 * @returns {Promise<string|null>}
	 */
	KVStoreClient.prototype.getEncryptedText = function (phrase)
	{
		return this.getEncrypted(phrase).then(function (bytes) { return bytes === null ? null : new TextDecoder().decode(bytes); });
	};

	// #region Static helpers
	/**
	 * Normalizes a phrase as typed by a user: lower case, words separated by single hyphens.  Spaces, hyphens, underscores, dots, and commas are all accepted as separators.
	 * @param {string} phrase
	 * @returns {string}
	 */
	KVStoreClient.normalizePhrase = function (phrase)
	{
		return String(phrase).toLowerCase().split(/[\s\-_.,]+/).filter(function (w) { return w.length > 0; }).join("-");
	};
	/**
	 * Generates a random phrase locally using a cryptographically secure random number generator.
	 * @param {string[]} wordList The EFF short wordlist #1 (1296 words).
	 * @param {number} [words] Number of words (default 6; 6 is the recommended minimum).
	 * @returns {string}
	 */
	KVStoreClient.generatePhrase = function (wordList, words)
	{
		words = words || 6;
		var n = wordList.length;
		// Rejection sampling avoids modulo bias.
		var limit = Math.floor(4294967296 / n) * n;
		var chosen = [];
		var buf = new Uint32Array(1);
		while (chosen.length < words)
		{
			crypto.getRandomValues(buf);
			if (buf[0] < limit)
				chosen.push(wordList[buf[0] % n]);
		}
		return chosen.join("-");
	};
	/**
	 * Derives the lookup key and content key from a phrase.  This is deliberately slow (600,000 PBKDF2 iterations, or 10,000 in "fast" mode).
	 * @param {string} phrase The secret phrase.  It is normalized first.
	 * @param {string} [mode] "standard" (the default) or "fast".  Data written with one mode can only be read with the same mode.
	 * @returns {Promise<{lookupKey: string, contentKey: CryptoKey}>}
	 */
	KVStoreClient.deriveKeys = function (phrase, mode)
	{
		var enc = new TextEncoder();
		var iterations = pbkdf2Iterations(mode);
		if (!iterations)
			return Promise.reject(new Error(unknownModeMessage(mode)));
		return crypto.subtle.importKey("raw", enc.encode(KVStoreClient.normalizePhrase(phrase)), "PBKDF2", false, ["deriveBits"]).then(function (baseKey)
		{
			return crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: enc.encode(SALT), iterations: iterations }, baseKey, 512);
		}).then(function (bits)
		{
			var material = new Uint8Array(bits);
			var lookupKey = KVStoreClient.base32Encode(material.subarray(0, 20));
			return crypto.subtle.importKey("raw", material.slice(32, 64), { name: "AES-GCM" }, false, ["encrypt", "decrypt"]).then(function (contentKey)
			{
				return { lookupKey: lookupKey, contentKey: contentKey };
			});
		});
	};
	/**
	 * Encrypts with AES-256-GCM.  Returns the 12-byte IV followed by the ciphertext (which includes the authentication tag).
	 * @param {CryptoKey} key
	 * @param {Uint8Array} bytes
	 * @returns {Promise<Uint8Array>}
	 */
	KVStoreClient.encrypt = function (key, bytes)
	{
		var iv = crypto.getRandomValues(new Uint8Array(12));
		return crypto.subtle.encrypt({ name: "AES-GCM", iv: iv }, key, bytes).then(function (ct)
		{
			var out = new Uint8Array(12 + ct.byteLength);
			out.set(iv, 0);
			out.set(new Uint8Array(ct), 12);
			return out;
		});
	};
	/**
	 * Decrypts data produced by encrypt().
	 * @param {CryptoKey} key
	 * @param {Uint8Array} data IV followed by ciphertext.
	 * @returns {Promise<Uint8Array>}
	 */
	KVStoreClient.decrypt = function (key, data)
	{
		if (data.length < 12 + 16)
			return Promise.reject(new Error("Encrypted data is too short."));
		return crypto.subtle.decrypt({ name: "AES-GCM", iv: data.subarray(0, 12) }, key, data.subarray(12)).then(function (pt) { return new Uint8Array(pt); });
	};
	/**
	 * RFC 4648 base32, lower case, unpadded.
	 * @param {Uint8Array} bytes
	 * @returns {string}
	 */
	KVStoreClient.base32Encode = function (bytes)
	{
		var out = "", buffer = 0, bits = 0;
		for (var i = 0; i < bytes.length; i++)
		{
			buffer = (buffer << 8) | bytes[i];
			bits += 8;
			while (bits >= 5)
			{
				bits -= 5;
				out += BASE32_ALPHABET[(buffer >>> bits) & 31];
			}
			buffer &= (1 << bits) - 1;
		}
		if (bits > 0)
			out += BASE32_ALPHABET[(buffer << (5 - bits)) & 31];
		return out;
	};
	/**
	 * Returns a random key suitable for putRaw: 20 random bytes as 32 base32 characters (160 bits).
	 * @returns {string}
	 */
	KVStoreClient.randomKey = function ()
	{
		return KVStoreClient.base32Encode(crypto.getRandomValues(new Uint8Array(20)));
	};
	/**
	 * Base64-encodes bytes (in chunks, so large arrays do not overflow the call stack).
	 * @param {Uint8Array} bytes
	 * @returns {string}
	 */
	KVStoreClient.bytesToBase64 = function (bytes)
	{
		var parts = [];
		for (var i = 0; i < bytes.length; i += 0x8000)
			parts.push(String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)));
		return btoa(parts.join(""));
	};
	/**
	 * Decodes base64 to bytes.
	 * @param {string} b64
	 * @returns {Uint8Array}
	 */
	KVStoreClient.base64ToBytes = function (b64)
	{
		var bin = atob(b64);
		var bytes = new Uint8Array(bin.length);
		for (var i = 0; i < bin.length; i++)
			bytes[i] = bin.charCodeAt(i);
		return bytes;
	};
	// #endregion

	KVStoreClient.KVStoreError = KVStoreError;
	root.KVStoreClient = KVStoreClient;
	if (typeof module === "object" && module.exports)
		module.exports = KVStoreClient;
})(typeof self !== "undefined" ? self : this);
