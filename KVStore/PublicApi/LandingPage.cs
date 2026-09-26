using BPUtil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Builds the public landing page, which describes the service and carries the Terms of Service and abuse contact.
	/// </summary>
	public static class LandingPage
	{
		/// <summary>
		/// Returns the landing page HTML.
		/// </summary>
		/// <param name="s">Current settings.</param>
		/// <returns></returns>
		public static string GetHtml(Settings s)
		{
			string operatorName = string.IsNullOrWhiteSpace(s.operatorName) ? "the operator of this service" : s.operatorName.Trim();
			string operatorHtml = WebUtility.HtmlEncode(operatorName);
			string abuseHtml;
			string abuse = s.abuseContact?.Trim();
			if (string.IsNullOrEmpty(abuse))
				abuseHtml = "<em>(no abuse contact has been configured)</em>";
			else if (abuse.Contains('@') && !abuse.Contains(' ') && !abuse.Contains(':'))
				abuseHtml = "<a href=\"mailto:" + WebUtility.HtmlEncode(abuse) + "\">" + WebUtility.HtmlEncode(abuse) + "</a>";
			else if (abuse.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
				abuseHtml = "<a href=\"" + WebUtility.HtmlEncode(abuse) + "\" rel=\"noreferrer\">" + WebUtility.HtmlEncode(abuse) + "</a>";
			else
				abuseHtml = WebUtility.HtmlEncode(abuse);

			BucketConfig defaultBucket = s.GetDefaultBucket();
			int maxTtl = s.buckets.Where(b => b.enabled).Select(b => b.maxTtlSeconds).DefaultIfEmpty(defaultBucket?.maxTtlSeconds ?? 7200).Max();
			string maxTtlText = FormatDuration(maxTtl);
			string maxSizeText = defaultBucket == null ? "" : StringUtil.FormatDiskBytes(defaultBucket.maxItemSizeBytes);

			StringBuilder sb = new StringBuilder();
			sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<meta name=""robots"" content=""noindex, nofollow"">
<title>KVStore</title>
<style>
:root { color-scheme: light dark; --fg: #1d1f23; --bg: #fafafa; --muted: #5b616b; --accent: #2458c7; --code: #eceef2; }
@media (prefers-color-scheme: dark) { :root { --fg: #e6e8eb; --bg: #16181c; --muted: #a0a6b0; --accent: #7aa2ff; --code: #262a31; } }
body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, -apple-system, ""Segoe UI"", Roboto, sans-serif; }
main { max-width: 760px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 1.8em; margin: 0 0 4px; }
h2 { font-size: 1.2em; margin: 2em 0 0.5em; }
p.lead { color: var(--muted); margin-top: 0; }
a { color: var(--accent); }
code, pre { background: var(--code); border-radius: 4px; font: 0.9em/1.45 ui-monospace, Consolas, monospace; }
code { padding: 1px 4px; }
pre { padding: 10px 12px; overflow-x: auto; }
table { border-collapse: collapse; width: 100%; font-size: 0.95em; }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--code); vertical-align: top; }
ol li, ul li { margin: 0.3em 0; }
</style>
</head>
<body>
<main>
<h1>KVStore</h1>
<p class=""lead"">A public, account-free, short-lived key/value store for moving small blobs of data between your own devices.</p>

<h2>How it works</h2>
<p>An app on one device uploads a value under a long random key, then shows you that key (for example as a QR code or a word phrase).  You enter the key on another device, and the app there downloads the value.  There are no accounts.  <strong>Anyone who knows a key can read, overwrite, or delete its value</strong>, so keys must be long and random, and apps should encrypt values before uploading them.</p>
<p>Every item is deleted automatically when it expires, at most ");
			sb.Append(WebUtility.HtmlEncode(maxTtlText));
			sb.Append(@" after it was stored.  Values can not be listed, searched, or downloaded by link.</p>

<h2>API</h2>
<p>All operations are <code>POST</code> requests with a JSON body, sent to <code>/v1/&lt;operation&gt;</code>.  Send <code>Content-Type: text/plain</code> from browsers to avoid a CORS preflight request.  Keys are 32 base32 characters (<code>a-z</code>, <code>2-7</code>).  Values are base64.</p>
<table>
<tr><th>Operation</th><th>Request body</th><th>Response</th></tr>
<tr><td><code>put</code></td><td><code>{""bucket"", ""key"", ""value"", ""ttl""}</code></td><td><code>{ok, expires, ttl, size}</code></td></tr>
<tr><td><code>get</code></td><td><code>{""bucket"", ""key""}</code></td><td><code>{ok, value, expires, size}</code></td></tr>
<tr><td><code>info</code></td><td><code>{""bucket"", ""key""}</code></td><td><code>{ok, exists, expires, size}</code></td></tr>
<tr><td><code>del</code></td><td><code>{""bucket"", ""key""}</code></td><td><code>{ok, deleted}</code></td></tr>
<tr><td><code>buckets</code></td><td><code>{}</code></td><td><code>{ok, defaultBucket, buckets}</code></td></tr>
<tr><td><code>phrase</code></td><td><code>{""words""}</code></td><td><code>{ok, phrase}</code></td></tr>
</table>
<p>""bucket"" is optional.  ""ttl"" is optional and is clamped to the bucket's limits.");
			if (maxSizeText != "")
			{
				sb.Append("  The default bucket accepts values up to ");
				sb.Append(WebUtility.HtmlEncode(maxSizeText));
				sb.Append('.');
			}
			sb.Append(@"  Requests are rate-limited per client.</p>

<h2>Terms of Service</h2>
<p>By using this service you agree to the following terms, offered by ");
			sb.Append(operatorHtml);
			sb.Append(@".</p>
<ol>
<li>The service is provided free of charge, as-is, without any warranty.  Stored data may be deleted or lost at any time, and the service may be changed, restricted, or shut down at any time without notice.</li>
<li>Do not store or transfer anything that is illegal, that you do not have the right to share, or that is harmful, including malware and content depicting the abuse of children.  Do not use the service to distribute files to the public, to harass anyone, or to attack or overload the service.</li>
<li>You are responsible for keeping your keys secret and for encrypting your data.  Anyone who knows a key can read, change, or delete its value.</li>
<li>The operator does not monitor stored content and can not list or search it, but will delete content reported through the abuse contact below and will report apparent child sexual abuse material to the appropriate authorities as required by law.</li>
<li>This service does not record the IP addresses of its users.  IP addresses are held in memory only briefly, for rate limiting.</li>
</ol>

<h2>Abuse and takedown requests</h2>
<p>Contact: ");
			sb.Append(abuseHtml);
			sb.Append(@"</p>
<p>Because stored items can not be listed or searched, a takedown request must include the exact bucket name and key of the item.</p>
</main>
</body>
</html>
");
			return sb.ToString();
		}
		private static string FormatDuration(int seconds)
		{
			if (seconds % 3600 == 0)
				return (seconds / 3600) + (seconds == 3600 ? " hour" : " hours");
			if (seconds % 60 == 0)
				return (seconds / 60) + (seconds == 60 ? " minute" : " minutes");
			return seconds + " seconds";
		}
	}
}
