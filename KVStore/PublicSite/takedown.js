/*
 * The self-service takedown form.  It deletes one item using the public API's "del" operation, and explains the outcome in plain language.
 */
(function ()
{
	"use strict";
	var $ = KVSite.$;

	function formatDuration(seconds)
	{
		if (seconds % 3600 === 0)
			return (seconds / 3600) + (seconds === 3600 ? " hour" : " hours");
		if (seconds % 60 === 0)
			return (seconds / 60) + (seconds === 60 ? " minute" : " minutes");
		return seconds + " seconds";
	}

	/**
	 * Shows an outcome message.
	 * @param {string} kind "ok", "warn", or "err"
	 * @param {string} title
	 * @param {string[]} paragraphs Plain text (never HTML).
	 * @param {string} [footnote] Small print, e.g. an error reference.
	 */
	function showResult(kind, title, paragraphs, footnote)
	{
		var box = document.createElement("div");
		box.className = "result " + kind;
		box.setAttribute("role", kind === "ok" ? "status" : "alert");
		var h = document.createElement("h3");
		h.textContent = title;
		box.appendChild(h);
		paragraphs.forEach(function (text)
		{
			var p = document.createElement("p");
			p.textContent = text;
			box.appendChild(p);
		});
		if (footnote)
		{
			var small = document.createElement("p");
			small.className = "small muted";
			small.textContent = footnote;
			box.appendChild(small);
		}
		var result = $("result");
		result.textContent = "";
		result.appendChild(box);
	}

	function setBusy(busy)
	{
		$("btnDelete").disabled = busy;
		$("key").readOnly = busy;
		$("bucket").readOnly = busy;
		$("busy").hidden = !busy;
	}

	/**
	 * Explains an API response to someone who has never heard of an API.
	 */
	function explain(status, json, retryAfter, bucket)
	{
		var inBucket = bucket ? " in the “" + bucket + "” bucket" : "";
		var code = json && json.error;
		if (status === 200 && json && json.ok)
		{
			if (json.deleted)
				showResult("ok", "Deleted.", ["The item has been permanently deleted.  Anyone who tries to open it now will find nothing there."]);
			else
				showResult("warn", "Nothing was deleted.", [
					"There is no item with that key" + inBucket + " right now.  It may already have been deleted, or it may have expired and deleted itself.",
					"If you believe it's still there, check that you copied the key exactly" + (bucket ? " and that the bucket name is right." : ".  If you were given a bucket name, enter it too.")
				]);
			return;
		}
		switch (code)
		{
			case "invalid_key":
				showResult("err", "That key isn't valid.", ["Check that you copied the whole key exactly, with nothing missing or added.  Keys are usually 32 characters long and use only letters and the digits 2 to 7."]);
				return;
			case "invalid_bucket":
				showResult("err", "That bucket name isn't valid.", ["Bucket names use only letters and the digits 2 to 7, up to 32 characters.  If you weren't given a bucket name, leave that box empty."]);
				return;
			case "unknown_bucket":
				showResult("err", "There's no bucket with that name.", ["Check the spelling of the bucket name.  If you weren't given a bucket name, leave that box empty."]);
				return;
			case "bucket_disabled":
				showResult("warn", "That bucket is switched off right now.", ["The people who run this service have temporarily switched off the" + (bucket ? " “" + bucket + "”" : "") + " bucket, so nothing in it can be opened or deleted at the moment.  Everything in it will still delete itself when it expires."]);
				return;
			case "rate_limited":
				showResult("warn", "Please wait a moment.", ["You've sent several requests in a short time, so the service is asking you to slow down.  Please try again in " + (retryAfter > 0 ? KVSite.describeSeconds(retryAfter) : "a few minutes") + "."]);
				return;
			case "bad_request":
				showResult("err", "The request wasn't understood.", ["Please check what you entered and try again."]);
				return;
		}
		if (status >= 500)
			showResult("err", "Something went wrong on the server.", ["The service had a problem and couldn't finish your request.  Please try again in a few minutes."], json && json.correlationId ? "Error reference: " + json.correlationId : null);
		else
			showResult("err", "Something unexpected happened.", ["The service answered in a way this page didn't expect, so it isn't clear whether anything was deleted.  Please try again later."], "HTTP status " + status + (code ? ", error “" + code + "”" : ""));
	}

	KVSite.loadBuckets("bucketList").then(function (json)
	{
		if (json && json.buckets.length)
			$("maxTtl").textContent = formatDuration(Math.max.apply(null, json.buckets.map(function (b) { return b.maxTtl; })));
	});

	$("takedownForm").addEventListener("submit", function (e)
	{
		e.preventDefault();
		var key = $("key").value.replace(/\s+/g, "");
		var bucket = $("bucket").value.trim();
		$("key").value = key;
		$("bucket").value = bucket;
		if (!key)
		{
			showResult("err", "Please enter the key.", ["The key is needed to find the item.  See “What you need” above."]);
			$("key").focus();
			return;
		}
		var body = { key: key };
		if (bucket)
			body.bucket = bucket;
		$("result").textContent = "";
		setBusy(true);
		KVSite.postJson("del", body).then(function (r)
		{
			var retryAfter = parseInt(r.response.headers.get("Retry-After"), 10) || 0;
			return KVSite.readJson(r.response).then(function (json)
			{
				explain(r.response.status, json, retryAfter, bucket);
			});
		}).catch(function (err)
		{
			if (err && err.kind === "timeout")
				showResult("err", "The service didn't respond.", ["It took too long to answer, so it isn't clear whether anything was deleted.  Please try again."]);
			else
				showResult("err", "Couldn't reach the service.", ["Check your internet connection and try again."]);
		}).then(function ()
		{
			setBusy(false);
		});
	});
})();
