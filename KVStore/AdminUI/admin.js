"use strict";
// KVStore Admin Console.  Plain JavaScript; the Content-Security-Policy forbids inline scripts and styles, so all DOM is built with createElement and styled with classes.
(function ()
{
	var config = null;
	var dashTimer = null;

	// #region Helpers
	function $(id) { return document.getElementById(id); }
	function el(tag, attrs)
	{
		var e = document.createElement(tag);
		if (attrs)
		{
			for (var k in attrs)
			{
				if (!attrs.hasOwnProperty(k) || attrs[k] === undefined || attrs[k] === null) continue;
				if (k === "text") e.textContent = attrs[k];
				else if (k === "className") e.className = attrs[k];
				else if (k === "onclick") e.addEventListener("click", attrs[k]);
				else e.setAttribute(k, attrs[k]);
			}
		}
		for (var i = 2; i < arguments.length; i++)
		{
			var c = arguments[i];
			if (c === null || c === undefined) continue;
			e.appendChild(typeof c === "string" || typeof c === "number" ? document.createTextNode(String(c)) : c);
		}
		return e;
	}
	function clear(e) { while (e.firstChild) e.removeChild(e.firstChild); return e; }
	function toast(message, ok)
	{
		var t = el("div", { className: "toast " + (ok ? "ok" : "err"), text: message });
		$("toasts").appendChild(t);
		setTimeout(function () { t.remove(); }, ok ? 5000 : 12000);
	}
	function fmtInt(n) { return n === null || n === undefined ? "–" : Math.round(n).toLocaleString(); }
	function fmtBytes(n)
	{
		if (n === null || n === undefined || n < 0) return "–";
		var units = ["B", "KiB", "MiB", "GiB", "TiB"];
		var i = 0;
		while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
		return (i === 0 ? n.toFixed(0) : n.toFixed(n < 10 ? 2 : 1)) + " " + units[i];
	}
	function fmtDuration(sec)
	{
		sec = Math.max(0, Math.floor(sec));
		var d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
		if (d > 0) return d + "d " + h + "h " + m + "m";
		if (h > 0) return h + "h " + m + "m";
		if (m > 0) return m + "m " + s + "s";
		return s + "s";
	}
	function fmtTime(unix) { return unix ? new Date(unix * 1000).toLocaleString() : "never"; }
	function pct(part, whole) { return whole > 0 ? part / whole * 100 : 0; }
	function meter(percent)
	{
		var m = el("span", { className: "meter" + (percent >= 90 ? " high" : "") });
		var fill = el("span");
		fill.style.width = Math.min(100, Math.max(0, percent)).toFixed(1) + "%";
		m.appendChild(fill);
		return el("span", null, m, percent.toFixed(1) + "%");
	}
	function histogram(values, labels)
	{
		var max = Math.max.apply(null, values.concat([1]));
		var h = el("span", { className: "hist", title: labels.map(function (l, i) { return l + ": " + fmtInt(values[i]); }).join("\n") });
		values.forEach(function (v)
		{
			var bar = el("span");
			bar.style.height = Math.max(4, v / max * 100) + "%";
			if (v === 0) bar.style.opacity = "0.25";
			h.appendChild(bar);
		});
		return h;
	}
	function num(id) { return Number($(id).value); }

	/** POSTs a JSON body to an Admin Console API method and returns the parsed response.  Throws an Error with the server's message on failure. */
	function api(path, data)
	{
		// Build an absolute URL from location.origin, which never contains credentials.  (fetch refuses relative URLs when the page was opened as http://user:pass@host/.)
		var url = location.origin + location.pathname.replace(/[^\/]*$/, "") + path;
		return fetch(url, {
			method: "POST",
			headers: { "Content-Type": "application/json", "X-KVStore-CSRF-Protection": "1" },
			body: JSON.stringify(data || {}),
			credentials: "same-origin",
			cache: "no-store"
		}).then(function (response)
		{
			return response.text().then(function (text)
			{
				var obj = null;
				try { obj = JSON.parse(text); } catch (e) { }
				if (!obj) throw new Error("HTTP " + response.status + ": " + (text || response.statusText));
				if (!obj.success) throw new Error(obj.error || "Unknown error");
				return obj;
			});
		});
	}
	/** Runs an API call from a form or button, disabling the button and reporting the result. */
	function run(button, promiseFn, onSuccess)
	{
		if (button) button.disabled = true;
		return promiseFn().then(function (r)
		{
			if (r && r.message) toast(r.message, true);
			if (onSuccess) onSuccess(r);
		}).catch(function (e) { toast(e.message, false); })
			.then(function () { if (button) button.disabled = false; });
	}
	// #endregion

	// #region Navigation
	function showPage()
	{
		var page = (location.hash || "#dashboard").substring(1);
		if (!$("page-" + page)) page = "dashboard";
		document.querySelectorAll(".page").forEach(function (p) { p.classList.toggle("hidden", p.id !== "page-" + page); });
		document.querySelectorAll("#nav a").forEach(function (a) { a.classList.toggle("active", a.getAttribute("data-page") === page); });
		if (dashTimer) { clearInterval(dashTimer); dashTimer = null; }
		if (page === "dashboard")
		{
			loadDashboard();
			dashTimer = setInterval(function () { if ($("autoRefresh").checked && !document.hidden) loadDashboard(); }, 5000);
		}
		else
			loadConfig();
	}
	// #endregion

	// #region Dashboard
	function tile(label, value, sub, bad)
	{
		return el("div", { className: "tile" + (bad ? " bad" : "") },
			el("div", { className: "label", text: label }),
			el("div", { className: "value", text: value }),
			sub ? el("div", { className: "sub", text: sub }) : null);
	}
	function loadDashboard()
	{
		api("Dashboard/Get").then(renderDashboard).catch(function (e) { $("dashUpdated").textContent = "Update failed: " + e.message; });
	}
	function renderDashboard(d)
	{
		$("version").textContent = "v" + d.version;
		$("dashUpdated").textContent = "Updated " + new Date().toLocaleTimeString();

		// Database recovery events are shown prominently.
		var banner = $("recoveryBanner");
		banner.className = "banner show " + (d.recovery.count > 0 ? "bad" : "good");
		banner.textContent = d.recovery.count > 0
			? "Database recovery events: " + d.recovery.count + ".  Last recovery: " + fmtTime(d.recovery.lastUnix) + ".  " + (d.recovery.lastReason || "")
			: "Database healthy: no recovery events have occurred.";

		var g = d.global;
		var tiles = clear($("tiles"));
		tiles.appendChild(tile("Recovery events", fmtInt(d.recovery.count), "last: " + fmtTime(d.recovery.lastUnix), d.recovery.count > 0));
		tiles.appendChild(tile("Uptime", fmtDuration(d.uptimeSeconds)));
		tiles.appendChild(tile("Items", fmtInt(g.itemCount), pct(g.itemCount, g.maxItemCount).toFixed(1) + "% of " + fmtInt(g.maxItemCount)));
		tiles.appendChild(tile("Stored bytes", fmtBytes(g.bytes), pct(g.bytes, g.maxTotalBytes).toFixed(1) + "% of " + fmtBytes(g.maxTotalBytes)));
		tiles.appendChild(tile("Item size (avg)", fmtBytes(g.stats.avgSize), "min " + fmtBytes(g.stats.minSize) + " / max " + fmtBytes(g.stats.maxSize)));
		tiles.appendChild(tile("Disk free", fmtBytes(d.process.diskFreeBytes)));
		tiles.appendChild(tile("Managed heap", fmtBytes(d.process.managedHeapBytes), "GC gen2: " + fmtInt(d.process.gen2Collections)));
		var c = d.counters;
		tiles.appendChild(tile("Requests (1 h)", fmtInt(c.Requests.last1h), fmtInt(c.Requests.last1m) + " in the last minute"));

		// Per-bucket statistics
		var t = clear($("bucketStats"));
		t.appendChild(el("thead", null, el("tr", null,
			el("th", { text: "Bucket" }), el("th", { text: "State" }), el("th", { className: "num", text: "Items" }), el("th", { text: "Count quota" }),
			el("th", { className: "num", text: "Bytes" }), el("th", { text: "Bytes quota" }), el("th", { className: "num", text: "Avg size" }),
			el("th", { className: "num", text: "Min" }), el("th", { className: "num", text: "Max" }), el("th", { text: "Sizes" }), el("th", { text: "Expiring in" }),
			el("th", { className: "num", text: "Expired, unswept" }))));
		var tb = el("tbody");
		d.buckets.concat([{ name: "(all buckets)", enabled: true, itemCount: g.itemCount, bytes: g.bytes, maxItemCount: g.maxItemCount, maxTotalBytes: g.maxTotalBytes, stats: g.stats, isTotal: true }]).forEach(function (b)
		{
			var s = b.stats;
			tb.appendChild(el("tr", null,
				el("td", null, b.isTotal ? el("strong", { text: b.name }) : b.name, b.isDefault ? el("span", { className: "muted", text: " (default)" }) : null),
				el("td", null, b.isTotal ? "" : el("span", { className: "pill " + (b.enabled ? "on" : "off"), text: b.enabled ? "enabled" : "disabled" })),
				el("td", { className: "num", text: fmtInt(b.itemCount) }),
				el("td", null, meter(pct(b.itemCount, b.maxItemCount))),
				el("td", { className: "num", text: fmtBytes(b.bytes) }),
				el("td", null, meter(pct(b.bytes, b.maxTotalBytes))),
				el("td", { className: "num", text: s.count ? fmtBytes(s.avgSize) : "–" }),
				el("td", { className: "num", text: s.count ? fmtBytes(s.minSize) : "–" }),
				el("td", { className: "num", text: s.count ? fmtBytes(s.maxSize) : "–" }),
				el("td", null, histogram(s.sizeHistogram, d.sizeHistogramLabels)),
				el("td", null, histogram(s.expiryHistogram, d.expiryHistogramLabels)),
				el("td", { className: "num", text: fmtInt(s.expiredPendingSweep) })));
		});
		t.appendChild(tb);
		var note = "Size and expiry statistics are computed from a scan of the metadata at most every 10 seconds (this scan is " + Math.round(d.itemStatsAgeMs / 1000) + " s old).  Hover over a histogram for its values.  Size bins: " + d.sizeHistogramLabels.join(", ") + ".  Expiry bins: " + d.expiryHistogramLabels.join(", ") + ".";
		if (d.unconfiguredBucketsWithItems.length)
			note += "  Items remain under unconfigured bucket names (unreachable; they will expire): " + d.unconfiguredBucketsWithItems.join(", ") + ".";
		$("itemStatsNote").textContent = note;

		// Operation counters
		var rows = [
			["Requests", "Requests"], ["put", "Puts"], ["get", "Gets"], ["del", "Dels"], ["info", "Infos"], ["buckets", "BucketLists"], ["phrase", "Phrases"], ["health", "Healths"],
			["400 responses", "Status400"], ["404 responses", "Status404"], ["413 responses", "Status413"], ["429 responses", "Status429"], ["503 responses", "Status503"], ["Other 5xx responses", "Status5xx"],
			["Bytes in", "BytesIn", true], ["Bytes out", "BytesOut", true],
			["Expired items swept", "ExpiredSwept"], ["Quota evictions", "QuotaEvicted"], ["Orphan files reclaimed", "OrphanFilesReclaimed"], ["Orphan metadata removed", "OrphanMetadataRemoved"],
			["Failed admin logins", "AdminAuthFailures"]
		];
		var ct = clear($("counters"));
		ct.appendChild(el("thead", null, el("tr", null, el("th", { text: "Counter" }), el("th", { className: "num", text: "1 min" }), el("th", { className: "num", text: "1 hour" }), el("th", { className: "num", text: "24 hours" }), el("th", { className: "num", text: "Since start" }))));
		var cb = el("tbody");
		rows.forEach(function (r)
		{
			var v = c[r[1]];
			var f = r[2] ? fmtBytes : fmtInt;
			cb.appendChild(el("tr", null, el("td", { text: r[0] }), el("td", { className: "num", text: f(v.last1m) }), el("td", { className: "num", text: f(v.last1h) }), el("td", { className: "num", text: f(v.last24h) }), el("td", { className: "num", text: f(v.total) })));
		});
		ct.appendChild(cb);

		// Rate limiters
		var lt = clear($("limiters"));
		lt.appendChild(el("thead", null, el("tr", null, el("th", { text: "Limiter" }), el("th", { className: "num", text: "Tracked clients" }), el("th", { text: "Ceiling utilization" }), el("th", { className: "num", text: "Capacity" }), el("th", { className: "num", text: "Refill per hour" }))));
		var lb = el("tbody");
		d.rateLimiters.forEach(function (l)
		{
			lb.appendChild(el("tr", null, el("td", { text: l.name }), el("td", { className: "num", text: fmtInt(l.trackedClients) + " / " + fmtInt(l.maxTrackedClients) }), el("td", null, meter(pct(l.trackedClients, l.maxTrackedClients))),
				el("td", { className: "num", text: fmtInt(l.capacity) }), el("td", { className: "num", text: fmtInt(l.refillRate * 3600) })));
		});
		lt.appendChild(lb);

		// Process
		var p = d.process;
		var pt = clear($("process"));
		var pb = el("tbody");
		[
			["Uptime", fmtDuration(d.uptimeSeconds)],
			["Managed heap (GC.GetTotalMemory)", fmtBytes(p.managedHeapBytes)],
			["GC heap size", fmtBytes(p.gcHeapSizeBytes)],
			["GC collections (gen0 / gen1 / gen2)", fmtInt(p.gen0Collections) + " / " + fmtInt(p.gen1Collections) + " / " + fmtInt(p.gen2Collections)],
			["Working set", fmtBytes(p.workingSetBytes)],
			["Threads", fmtInt(p.threadCount)],
			["Disk free on data volume", fmtBytes(p.diskFreeBytes)],
			["Database file size (with log)", fmtBytes(p.databaseFileBytes)],
			["Blob tree", p.lastOrphanSweepUnix ? fmtInt(p.blobTreeFiles) + " files, " + fmtBytes(p.blobTreeBytes) + " (measured by the orphan sweep at " + fmtTime(p.lastOrphanSweepUnix) + ")" : "not measured yet (the first orphan sweep runs a minute after startup)"]
		].forEach(function (r) { pb.appendChild(el("tr", null, el("td", { text: r[0] }), el("td", { text: r[1] }))); });
		pt.appendChild(pb);
	}
	// #endregion

	// #region Configuration
	function loadConfig()
	{
		return api("Config/Get").then(function (c)
		{
			config = c;
			renderBuckets();
			renderSettings();
		}).catch(function (e) { toast("Unable to load configuration: " + e.message, false); });
	}
	function renderBuckets()
	{
		var t = clear($("bucketList"));
		t.appendChild(el("thead", null, el("tr", null,
			el("th", { text: "Name" }), el("th", { text: "State" }), el("th", { className: "num", text: "Default TTL" }), el("th", { className: "num", text: "Max TTL" }),
			el("th", { className: "num", text: "Max item size" }), el("th", { className: "num", text: "Max items" }), el("th", { className: "num", text: "Max total" }), el("th", { text: "Notes" }), el("th", { text: "" }))));
		var tb = el("tbody");
		config.buckets.forEach(function (b)
		{
			var isDefault = b.name === config.defaultBucketName;
			tb.appendChild(el("tr", null,
				el("td", null, b.name, isDefault ? el("span", { className: "muted", text: " (default)" }) : null),
				el("td", null, el("span", { className: "pill " + (b.enabled ? "on" : "off"), text: b.enabled ? "enabled" : "disabled" })),
				el("td", { className: "num", text: fmtDuration(b.defaultTtlSeconds) }),
				el("td", { className: "num", text: fmtDuration(b.maxTtlSeconds) }),
				el("td", { className: "num", text: fmtBytes(b.maxItemSizeBytes) }),
				el("td", { className: "num", text: fmtInt(b.maxItemCount) }),
				el("td", { className: "num", text: fmtBytes(b.maxTotalBytes) }),
				el("td", { text: b.notes || "" }),
				el("td", null,
					el("button", { type: "button", className: "link", text: "Edit", onclick: function () { editBucket(b); } }),
					isDefault ? null : el("button", { type: "button", className: "link", text: "Make default", onclick: function (e) { setDefaultBucket(e.target, b.name); } }),
					isDefault ? null : el("button", { type: "button", className: "link danger", text: "Delete", onclick: function (e) { deleteBucket(e.target, b.name); } }))));
		});
		t.appendChild(tb);

		var sel = clear($("fb_bucket"));
		config.buckets.forEach(function (b) { sel.appendChild(el("option", { value: b.name, text: b.name })); });
	}
	function editBucket(b)
	{
		var isNew = !b;
		if (isNew) { b = JSON.parse(JSON.stringify(config.bucketDefaults)); b.name = ""; b.enabled = true; b.notes = ""; }
		$("bucketFormTitle").textContent = isNew ? "New bucket" : "Edit bucket \"" + b.name + "\"";
		$("bf_originalName").value = isNew ? "" : b.name;
		$("bf_name").value = b.name;
		$("bf_enabled").checked = b.enabled;
		$("bf_enabled").disabled = !isNew && b.name === config.defaultBucketName;
		["defaultTtlSeconds", "maxTtlSeconds", "maxItemSizeBytes", "maxItemCount", "maxTotalBytes"].forEach(function (f) { $("bf_" + f).value = b[f]; });
		$("bf_notes").value = b.notes || "";
		$("bucketForm").classList.remove("hidden");
		$("bf_name").focus();
	}
	function readBucketFields(prefix)
	{
		return {
			defaultTtlSeconds: num(prefix + "defaultTtlSeconds"),
			maxTtlSeconds: num(prefix + "maxTtlSeconds"),
			maxItemSizeBytes: num(prefix + "maxItemSizeBytes"),
			maxItemCount: num(prefix + "maxItemCount"),
			maxTotalBytes: num(prefix + "maxTotalBytes")
		};
	}
	function setDefaultBucket(button, name)
	{
		run(button, function () { return api("Config/SetDefaultBucket", { name: name }); }, loadConfig);
	}
	function deleteBucket(button, name)
	{
		if (!confirm("Delete bucket \"" + name + "\" and ALL of its items?  This can not be undone."))
			return;
		run(button, function () { return api("Config/DeleteBucket", { name: name }); }, loadConfig);
	}
	function renderSettings()
	{
		var d = config.bucketDefaults;
		["defaultTtlSeconds", "maxTtlSeconds", "maxItemSizeBytes", "maxItemCount", "maxTotalBytes"].forEach(function (f) { $("gd_" + f).value = d[f]; });
		var rl = config.rateLimits;
		["writes", "reads", "bytes"].forEach(function (k)
		{
			$("rl_" + k + "_capacity").value = rl[k].capacity;
			$("rl_" + k + "_refill").value = +(rl[k].refillRate * 3600).toFixed(6);
		});
		$("rl_maxTrackedClients").value = rl.maxTrackedClients;
		$(config.permissiveKeys ? "kf_permissive" : "kf_strict").checked = true;
		var m = config.maintenance;
		["expirySweepIntervalSeconds", "orphanSweepIntervalSeconds", "orphanMinAgeSeconds", "minFreeDiskBytes"].forEach(function (f) { $("mt_" + f).value = m[f]; });
		$("gn_publicIpAddress").value = config.publicIpAddress || "";
		$("gn_publicHttpPort").value = config.publicHttpPort;
		$("gn_operatorName").value = config.operatorName || "";
		$("gn_abuseContact").value = config.abuseContact || "";
		var admin = [];
		if (config.adminHttpsPort > 0) admin.push("https port " + config.adminHttpsPort);
		if (config.adminHttpPort > 0) admin.push("http port " + config.adminHttpPort);
		$("adminBinding").textContent = (config.adminIpAddress || "all interfaces") + ", " + admin.join(", ");
	}
	// #endregion

	// #region Event wiring
	function onSubmit(formId, handler)
	{
		$(formId).addEventListener("submit", function (e)
		{
			e.preventDefault();
			handler(e.submitter || $(formId).querySelector("button[type=submit]"));
		});
	}
	document.addEventListener("DOMContentLoaded", function ()
	{
		window.addEventListener("hashchange", showPage);
		$("btnRefresh").addEventListener("click", loadDashboard);
		$("btnNewBucket").addEventListener("click", function () { editBucket(null); });
		$("bf_cancel").addEventListener("click", function () { $("bucketForm").classList.add("hidden"); });

		onSubmit("bucketForm", function (button)
		{
			var bucket = readBucketFields("bf_");
			bucket.name = $("bf_name").value.trim();
			bucket.enabled = $("bf_enabled").checked;
			bucket.notes = $("bf_notes").value;
			run(button, function () { return api("Config/SaveBucket", { originalName: $("bf_originalName").value, bucket: bucket }); }, function ()
			{
				$("bucketForm").classList.add("hidden");
				loadConfig();
			});
		});
		onSubmit("defaultsForm", function (button)
		{
			var d = readBucketFields("gd_");
			d.name = "default";
			d.enabled = true;
			run(button, function () { return api("Config/SaveBucketDefaults", d); }, loadConfig);
		});
		onSubmit("rateForm", function (button)
		{
			if (!confirm("Applying new rate limits resets every client's rate limit buckets to full.  Continue?"))
				return;
			var r = { maxTrackedClients: num("rl_maxTrackedClients") };
			["writes", "reads", "bytes"].forEach(function (k) { r[k] = { capacity: num("rl_" + k + "_capacity"), refillRate: num("rl_" + k + "_refill") / 3600 }; });
			run(button, function () { return api("Config/SaveRateLimits", r); }, loadConfig);
		});
		onSubmit("keyForm", function (button)
		{
			run(button, function () { return api("Config/SaveKeyFormat", { permissiveKeys: $("kf_permissive").checked }); }, loadConfig);
		});
		onSubmit("maintForm", function (button)
		{
			var m = {};
			["expirySweepIntervalSeconds", "orphanSweepIntervalSeconds", "orphanMinAgeSeconds", "minFreeDiskBytes"].forEach(function (f) { m[f] = num("mt_" + f); });
			run(button, function () { return api("Config/SaveMaintenance", m); }, loadConfig);
		});
		$("btnExpirySweep").addEventListener("click", function (e) { run(e.target, function () { return api("Operations/RunExpirySweep"); }); });
		$("btnOrphanSweep").addEventListener("click", function (e) { run(e.target, function () { return api("Operations/RunOrphanSweep"); }); });
		onSubmit("generalForm", function (button)
		{
			run(button, function ()
			{
				return api("Config/SaveGeneral", {
					publicIpAddress: $("gn_publicIpAddress").value.trim(),
					publicHttpPort: num("gn_publicHttpPort"),
					operatorName: $("gn_operatorName").value,
					abuseContact: $("gn_abuseContact").value
				});
			}, loadConfig);
		});
		onSubmit("takedownForm", function (button)
		{
			if (!confirm("Delete this item?  This can not be undone."))
				return;
			run(button, function () { return api("Operations/DeleteItem", { bucket: $("td_bucket").value.trim(), key: $("td_key").value.trim() }); }, function () { $("td_key").value = ""; });
		});
		onSubmit("flushBucketForm", function (button)
		{
			var name = $("fb_bucket").value;
			if (!name || !confirm("Delete ALL items in bucket \"" + name + "\"?  This can not be undone."))
				return;
			run(button, function () { return api("Operations/FlushBucket", { name: name }); });
		});
		onSubmit("flushAllForm", function (button)
		{
			if ($("fa_confirm").value !== "DELETE")
			{
				toast("Type DELETE to confirm.", false);
				return;
			}
			run(button, function () { return api("Operations/FlushAll"); }, function () { $("fa_confirm").value = ""; });
		});
		onSubmit("rebuildForm", function (button)
		{
			if (!confirm("Rebuild the database now?  Storage operations will wait until it is finished."))
				return;
			run(button, function () { return api("Operations/RebuildDatabase"); });
		});

		showPage();
	});
	// #endregion
})();
