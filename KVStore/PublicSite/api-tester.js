/*
 * The API tester.  Builds a request for any public API operation from form fields, sends it, and shows the request and the response.
 */
(function ()
{
	"use strict";
	var $ = KVSite.$;
	/** Values longer than this many characters are shortened for display. */
	var PREVIEW_CHARS = 50;
	var ELLIPSIS = "…";
	var ops = {
		put: "Stores a value under a key, replacing any value already there.  The value travels as base64 text inside JSON.",
		putraw: "Stores a value like put, but the request body is the value's raw bytes and the other fields are URL parameters.",
		get: "Reads a value.  The value arrives as base64 text inside JSON.",
		getraw: "Reads a value.  The response body is the value's raw bytes, with its expiration time and size in headers.",
		info: "Reports whether a value exists, and its size and expiration time, without downloading it.",
		del: "Deletes a value immediately.",
		buckets: "Lists the enabled buckets and their limits.",
		phrase: "Generates random words for use as a secret phrase.",
		health: "Reports that the service is running."
	};
	var statusTexts = { 200: "OK", 204: "No Content", 400: "Bad Request", 404: "Not Found", 405: "Method Not Allowed", 413: "Content Too Large", 429: "Too Many Requests", 500: "Internal Server Error", 503: "Service Unavailable" };
	var shownHeaders = ["Content-Type", "Content-Length", "KV-Expires", "KV-Size", "Retry-After"];

	function currentOp()
	{
		return $("op").value;
	}
	function valueFormat()
	{
		return document.querySelector("input[name=valueFormat]:checked").value;
	}

	// #region Form
	function updateForm()
	{
		var op = currentOp();
		document.querySelectorAll("[data-for]").forEach(function (el)
		{
			el.hidden = el.getAttribute("data-for").split(" ").indexOf(op) === -1;
		});
		$("opDesc").textContent = ops[op];
		$("opDocs").href = "api#op-" + op;
		var fmt = valueFormat();
		$("valueText").hidden = fmt === "file";
		$("valueFile").hidden = fmt !== "file";
		var hints = {
			text: op === "put" ? "Encoded as UTF-8, then as base64." : "Sent as UTF-8 bytes.",
			base64: op === "put" ? "Sent exactly as typed, so you can also see how invalid base64 is handled." : "Decoded here, then sent as bytes.",
			file: op === "put" ? "The file's bytes are sent as base64." : "The file's bytes are sent as the request body."
		};
		$("valueHint").textContent = hints[fmt];
		showFormError(null);
	}
	function showFormError(message)
	{
		$("formError").textContent = message || "";
		$("formError").hidden = !message;
	}
	/**
	 * Resolves with the value to store: { bytes } for putraw, { base64 } for put.  Rejects with a message for the user if the value can't be read.
	 */
	function readValue(op)
	{
		var fmt = valueFormat();
		if (fmt === "file")
		{
			var file = $("valueFile").files[0];
			if (!file)
				return Promise.reject("Choose a file, or pick another value format.");
			return file.arrayBuffer().then(function (ab)
			{
				var bytes = new Uint8Array(ab);
				return op === "put" ? { base64: KVSite.bytesToBase64(bytes) } : { bytes: bytes };
			});
		}
		var text = $("valueText").value;
		if (fmt === "base64")
		{
			if (op === "put")
				return Promise.resolve({ base64: text });
			var decoded = KVSite.base64ToBytes(text);
			if (!decoded)
				return Promise.reject("That isn't valid base64, so it can't be decoded into bytes for putraw.  Use put to see how the server handles invalid base64.");
			return Promise.resolve({ bytes: decoded });
		}
		var utf8 = new TextEncoder().encode(text);
		return Promise.resolve(op === "put" ? { base64: KVSite.bytesToBase64(utf8) } : { bytes: utf8 });
	}
	/**
	 * Resolves with { method, path, body, contentType, preview } for the current form.
	 */
	function buildRequest()
	{
		var op = currentOp();
		var key = $("key").value.trim();
		var bucket = $("bucket").value.trim();
		var ttl = $("ttl").value.trim();
		var words = $("words").value.trim();
		var json = {};
		if (op === "health")
			return Promise.resolve({ method: "GET", path: "health", preview: "" });
		if (op === "buckets")
			return Promise.resolve(jsonRequest(op, json));
		if (op === "phrase")
		{
			if (words !== "")
				json.words = Number(words);
			return Promise.resolve(jsonRequest(op, json));
		}
		if (op === "putraw")
		{
			return readValue(op).then(function (v)
			{
				var params = [];
				if (key)
					params.push("key=" + encodeURIComponent(key));
				if (bucket)
					params.push("bucket=" + encodeURIComponent(bucket));
				if (ttl !== "")
					params.push("ttl=" + encodeURIComponent(ttl));
				// No Content-Type: a Uint8Array body gets none by default, so the browser needs no CORS preflight request.
				return { method: "POST", path: "putraw" + (params.length ? "?" + params.join("&") : ""), body: v.bytes, contentType: null, preview: describeBytes(v.bytes) };
			});
		}
		if (key)
			json.key = key;
		if (bucket)
			json.bucket = bucket;
		if (op !== "put")
			return Promise.resolve(jsonRequest(op, json));
		return readValue(op).then(function (v)
		{
			json.value = v.base64;
			if (ttl !== "")
				json.ttl = Number(ttl);
			return jsonRequest(op, json);
		});
	}
	function jsonRequest(op, json)
	{
		var shown = JSON.parse(JSON.stringify(json));
		var note = "";
		if (typeof shown.value === "string" && shown.value.length > PREVIEW_CHARS)
		{
			note = "\n\n(\"value\" is shortened here to its first " + PREVIEW_CHARS + " of " + shown.value.length.toLocaleString() + " characters.)";
			shown.value = shown.value.substring(0, PREVIEW_CHARS) + ELLIPSIS;
		}
		return { method: "POST", path: op, body: JSON.stringify(json), contentType: "text/plain;charset=UTF-8", preview: JSON.stringify(shown, null, 2) + note };
	}
	// #endregion

	// #region Display
	/**
	 * Describes bytes for display: as text if they are valid UTF-8, otherwise as base64, shortened to PREVIEW_CHARS characters.
	 */
	function describeBytes(bytes)
	{
		var size = KVSite.formatBytes(bytes.length);
		if (bytes.length === 0)
			return "(empty: 0 bytes)";
		var text = null;
		try
		{
			text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
		}
		catch (e) { }
		if (text !== null)
			return "(" + size + ", shown as UTF-8 text" + (shorten(text) !== text ? ", shortened" : "") + ")\n" + shorten(text);
		var b64 = KVSite.bytesToBase64(bytes.length > 64 ? bytes.subarray(0, 64) : bytes);
		return "(" + size + ", not text, so shown as base64" + (bytes.length > 64 || b64.length > PREVIEW_CHARS ? ", shortened" : "") + ")\n" + shorten(b64, bytes.length > 64);
	}
	function shorten(text, forceEllipsis)
	{
		var chars = Array.from(text);
		if (chars.length > PREVIEW_CHARS)
			return chars.slice(0, PREVIEW_CHARS).join("") + ELLIPSIS;
		return forceEllipsis ? text + ELLIPSIS : text;
	}
	function addNote(text)
	{
		var p = document.createElement("p");
		p.className = "note";
		p.textContent = text;
		$("notes").appendChild(p);
	}
	function showRequest(req)
	{
		var url = new URL("v1/" + req.path, location.href);
		var lines = [req.method + " " + url.pathname + url.search];
		if (req.contentType)
			lines.push("Content-Type: " + req.contentType);
		var text = lines.join("\n");
		if (req.preview)
			text += "\n\n" + req.preview;
		$("requestText").textContent = text;
	}
	function showResponse(op, response, elapsedMs)
	{
		var status = response.status;
		$("status").textContent = status + " " + (response.statusText || statusTexts[status] || "");
		$("status").className = "status " + (status >= 200 && status < 300 ? "ok" : "err");
		var headerLines = [];
		shownHeaders.forEach(function (name)
		{
			var v = response.headers.get(name);
			if (v !== null)
				headerLines.push(name + ": " + v);
		});
		$("responseHeaders").textContent = headerLines.join("\n");
		$("responseHeaders").hidden = headerLines.length === 0;
		var retryAfter = parseInt(response.headers.get("Retry-After"), 10);
		if (retryAfter > 0)
			addNote("Retry-After: wait " + KVSite.describeSeconds(retryAfter) + " before trying again.");

		if (op === "getraw" && status === 200)
		{
			return response.arrayBuffer().then(function (ab)
			{
				var bytes = new Uint8Array(ab);
				$("timing").textContent = elapsedMs + " ms";
				$("responseBody").textContent = describeBytes(bytes);
				var expires = parseInt(response.headers.get("KV-Expires"), 10);
				if (expires > 0)
					addNote("KV-Expires: " + KVSite.describeUnixTime(expires) + ".");
			});
		}
		return response.text().then(function (text)
		{
			$("timing").textContent = elapsedMs + " ms, " + KVSite.formatBytes(new TextEncoder().encode(text).length);
			var json = null;
			try
			{
				json = JSON.parse(text);
			}
			catch (e) { }
			if (!json || typeof json !== "object")
			{
				$("responseBody").textContent = text.length > 2000 ? text.substring(0, 2000) + ELLIPSIS : text;
				return;
			}
			if (typeof json.value === "string")
			{
				var full = json.value;
				if (full.length > PREVIEW_CHARS)
				{
					json.value = full.substring(0, PREVIEW_CHARS) + ELLIPSIS;
					addNote("\"value\" is shortened to its first " + PREVIEW_CHARS + " of " + full.length.toLocaleString() + " characters.");
				}
				var decoded = KVSite.base64ToBytes(full);
				if (decoded)
					addNote("Decoded value: " + describeBytes(decoded).replace("\n", " "));
			}
			if (typeof json.expires === "number")
				addNote("expires: " + KVSite.describeUnixTime(json.expires) + ".");
			$("responseBody").textContent = JSON.stringify(json, null, 2);
		});
	}
	function showNoResponse(err)
	{
		$("status").textContent = "No response";
		$("status").className = "status err";
		$("timing").textContent = "";
		$("responseHeaders").hidden = true;
		$("responseBody").textContent = err && err.kind === "timeout" ? "The server did not respond in time." : "The request could not be sent, or the connection failed.  Check your internet connection.";
	}
	function setBusy(busy)
	{
		$("btnSend").disabled = busy;
		$("busy").hidden = !busy;
	}
	// #endregion

	// #region Events
	$("op").addEventListener("change", function ()
	{
		history.replaceState(null, "", "#" + currentOp());
		updateForm();
	});
	document.querySelectorAll("input[name=valueFormat]").forEach(function (r) { r.addEventListener("change", updateForm); });
	$("btnRandomKey").addEventListener("click", function ()
	{
		$("key").value = KVSite.randomKey();
	});
	window.addEventListener("hashchange", function ()
	{
		var h = location.hash.substring(1);
		if (ops[h] && h !== currentOp())
		{
			$("op").value = h;
			updateForm();
		}
	});
	$("testerForm").addEventListener("submit", function (e)
	{
		e.preventDefault();
		showFormError(null);
		var op = currentOp();
		setBusy(true);
		buildRequest().then(function (req)
		{
			$("notes").textContent = "";
			showRequest(req);
			$("status").textContent = "";
			$("timing").textContent = "";
			$("responseHeaders").hidden = true;
			$("responseBody").textContent = "";
			$("output").hidden = false;
			return KVSite.request(req.method, req.path, req.body, req.contentType, 120000).then(function (res)
			{
				return showResponse(op, res.response, res.elapsedMs);
			}).catch(showNoResponse);
		}, function (message)
		{
			showFormError(typeof message === "string" ? message : "The value could not be read.");
		}).then(function ()
		{
			setBusy(false);
		});
	});
	// #endregion

	var initial = location.hash.substring(1);
	if (ops[initial])
		$("op").value = initial;
	if (!$("key").value)
		$("key").value = KVSite.randomKey();
	updateForm();
	KVSite.loadBuckets("bucketList");
})();
