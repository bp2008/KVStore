using BPUtil;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Builds the public web pages: the landing page, the API documentation, the API tester, and the takedown form.  Each page is an HTML file in the "PublicSite" folder whose <c>{{name}}</c> placeholders are filled in from the current settings.
	/// </summary>
	public static class PublicPages
	{
		/// <summary>
		/// Returns the HTML of a public page with its placeholders filled in, or null if there is no such page.
		/// </summary>
		/// <param name="page">"" for the landing page, otherwise the page's name without ".html", e.g. "api".</param>
		/// <param name="s">Current settings.</param>
		/// <returns></returns>
		public static string GetHtml(string page, Settings s)
		{
			string fileName;
			if (page == "")
				fileName = "index.html";
			else if (page.IEquals("index") || page.Contains('.') || page.Contains('/'))
				return null; // The landing page is only served at "/", and other files are requested by their full name.
			else
				fileName = page + ".html";
			if (!EmbeddedFiles.PublicSite.TryGet(fileName, out byte[] body, out string contentType))
				return null;
			StringBuilder sb = new StringBuilder(Encoding.UTF8.GetString(body));
			foreach (KeyValuePair<string, string> v in GetPlaceholderValues(s))
				sb.Replace("{{" + v.Key + "}}", WebUtility.HtmlEncode(v.Value));
			return sb.ToString();
		}
		private static Dictionary<string, string> GetPlaceholderValues(Settings s)
		{
			BucketConfig defaultBucket = s.GetDefaultBucket() ?? new BucketConfig() { name = s.defaultBucketName };
			int maxTtl = s.buckets.Where(b => b.enabled).Select(b => b.maxTtlSeconds).DefaultIfEmpty(defaultBucket.maxTtlSeconds).Max();
			return new Dictionary<string, string>()
			{
				["operatorName"] = string.IsNullOrWhiteSpace(s.operatorName) ? "the operator of this service" : s.operatorName.Trim(),
				["maxTtl"] = FormatDuration(maxTtl),
				["keyFormatNote"] = s.permissiveKeys ? "This server also accepts keys of " + KvNames.PermissiveKeyMinLength + " to " + KvNames.PermissiveKeyMaxLength + " characters made of letters, digits, underscores, periods, and hyphens.  Those keys are case-sensitive." : "",
				["defaultBucket"] = defaultBucket.name,
				["defaultMaxItemSize"] = StringUtil.FormatDiskBytes(defaultBucket.maxItemSizeBytes),
				["defaultTtl"] = FormatDuration(defaultBucket.defaultTtlSeconds),
				["defaultMaxTtl"] = FormatDuration(defaultBucket.maxTtlSeconds),
				["writeLimit"] = FormatNumber(s.rateLimits.writes.capacity) + " requests at once, then " + FormatNumber(s.rateLimits.writes.refillRate * 3600) + " more per hour",
				["readLimit"] = FormatNumber(s.rateLimits.reads.capacity) + " requests at once, then " + FormatNumber(s.rateLimits.reads.refillRate * 3600) + " more per hour",
				["byteLimit"] = StringUtil.FormatDiskBytes((long)s.rateLimits.bytes.capacity) + " at once, then " + StringUtil.FormatDiskBytes((long)Math.Round(s.rateLimits.bytes.refillRate * 3600)) + " more per hour"
			};
		}
		private static string FormatNumber(double d)
		{
			return Math.Round(d, 2).ToString("#,0.##", CultureInfo.InvariantCulture);
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
