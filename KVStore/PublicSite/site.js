/*
 * Helpers shared by the public web pages (the API tester and the takedown form).
 */
var KVSite = (function ()
{
	"use strict";

	var BASE32_ALPHABET = "abcdefghijklmnopqrstuvwxyz234567";

	function $(id)
	{
		return document.getElementById(id);
	}

	/**
	 * Sends a request to the API (on this same server) and resolves with { response, elapsedMs }.
	 * Rejects with an Error whose "kind" is "timeout" or "network" if no response arrives.
	 * @param {string} method "GET" or "POST"
	 * @param {string} path Path under /v1/, e.g. "put" or "putraw?key=...".
	 * @param {BodyInit} [body] Request body.
	 * @param {string} [contentType] Content-Type of the body.  Omitted if falsy.
	 * @param {number} [timeoutMs] Milliseconds to wait for the response headers (default 30000).
	 */
	function request(method, path, body, contentType, timeoutMs)
	{
		var controller = new AbortController();
		var timedOut = false;
		var timer = setTimeout(function () { timedOut = true; controller.abort(); }, timeoutMs || 30000);
		var headers = {};
		if (contentType)
			headers["Content-Type"] = contentType;
		var start = performance.now();
		return fetch("v1/" + path, {
			method: method,
			headers: headers,
			body: body,
			cache: "no-store",
			credentials: "omit",
			signal: controller.signal
		}).then(function (response)
		{
			clearTimeout(timer);
			return { response: response, elapsedMs: Math.round(performance.now() - start) };
		}, function (err)
		{
			clearTimeout(timer);
			var e = new Error(timedOut ? "The request timed out." : "The request failed: " + (err && err.message));
			e.kind = timedOut ? "timeout" : "network";
			throw e;
		});
	}

	/**
	 * POSTs a JSON body the way browsers should: as text/plain, which needs no CORS preflight.
	 */
	function postJson(endpoint, obj, timeoutMs)
	{
		return request("POST", endpoint, JSON.stringify(obj || {}), "text/plain;charset=UTF-8", timeoutMs);
	}

	/**
	 * Reads a JSON response body.  Resolves with null if the body is not JSON.
	 */
	function readJson(response)
	{
		return response.text().then(function (text)
		{
			try
			{
				return JSON.parse(text);
			}
			catch (e)
			{
				return null;
			}
		});
	}

	/**
	 * Loads the list of buckets and fills a datalist element with their names.  Resolves with the "buckets" response, or null on failure (which is not fatal to any page).
	 */
	function loadBuckets(datalistId)
	{
		return postJson("buckets", {}).then(function (r) { return readJson(r.response); }).then(function (json)
		{
			if (!json || !json.ok)
				return null;
			var list = $(datalistId);
			if (list)
			{
				list.textContent = "";
				json.buckets.forEach(function (b)
				{
					var opt = document.createElement("option");
					opt.value = b.name;
					if (b.name === json.defaultBucket)
						opt.label = b.name + " (default)";
					list.appendChild(opt);
				});
			}
			return json;
		}).catch(function () { return null; });
	}

	/**
	 * Returns a random key: 20 random bytes as 32 base32 characters.
	 */
	function randomKey()
	{
		var bytes = crypto.getRandomValues(new Uint8Array(20));
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
		return out;
	}

	/**
	 * Formats a number of seconds for people, e.g. "about 2 minutes".
	 */
	function describeSeconds(seconds)
	{
		seconds = Math.max(1, Math.round(seconds));
		if (seconds < 60)
			return seconds + (seconds === 1 ? " second" : " seconds");
		var minutes = Math.round(seconds / 60);
		if (minutes < 60)
			return "about " + minutes + (minutes === 1 ? " minute" : " minutes");
		var hours = Math.round(minutes / 60);
		return "about " + hours + (hours === 1 ? " hour" : " hours");
	}

	/**
	 * Formats a Unix time as local date and time, plus how far away it is.
	 */
	function describeUnixTime(unixSeconds)
	{
		var d = new Date(unixSeconds * 1000);
		var diff = unixSeconds - Date.now() / 1000;
		return d.toLocaleString() + (diff >= 0 ? " (in " + describeSeconds(diff).replace(/^about /, "") + ")" : " (" + describeSeconds(-diff).replace(/^about /, "") + " ago)");
	}

	function formatBytes(n)
	{
		if (n < 1024)
			return n + (n === 1 ? " byte" : " bytes");
		var units = ["KiB", "MiB", "GiB"];
		var v = n, i = -1;
		do
		{
			v /= 1024;
			i++;
		} while (v >= 1024 && i < units.length - 1);
		return (Math.round(v * 100) / 100) + " " + units[i];
	}

	/**
	 * Base64-encodes bytes (in chunks, so large arrays do not overflow the call stack).
	 */
	function bytesToBase64(bytes)
	{
		var parts = [];
		for (var i = 0; i < bytes.length; i += 0x8000)
			parts.push(String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)));
		return btoa(parts.join(""));
	}

	/**
	 * Decodes base64 (standard or URL-safe alphabet, padding optional, whitespace ignored), like the server does.  Returns null if the input is not valid base64.
	 */
	function base64ToBytes(text)
	{
		var b64 = text.replace(/\s+/g, "").replace(/-/g, "+").replace(/_/g, "/").replace(/=+$/, "");
		if (b64.length % 4 === 1 || /[^A-Za-z0-9+/]/.test(b64))
			return null;
		while (b64.length % 4)
			b64 += "=";
		try
		{
			var bin = atob(b64);
			var bytes = new Uint8Array(bin.length);
			for (var i = 0; i < bin.length; i++)
				bytes[i] = bin.charCodeAt(i);
			return bytes;
		}
		catch (e)
		{
			return null;
		}
	}

	return {
		$: $,
		request: request,
		postJson: postJson,
		readJson: readJson,
		loadBuckets: loadBuckets,
		randomKey: randomKey,
		describeSeconds: describeSeconds,
		describeUnixTime: describeUnixTime,
		formatBytes: formatBytes,
		bytesToBase64: bytesToBase64,
		base64ToBytes: base64ToBytes
	};
})();
