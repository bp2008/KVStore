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
	/// Builds the public landing page, which describes the service and its API and carries the Terms of Service.  The page is the template "PublicSite/index.html", whose <c>{{name}}</c> placeholders are filled in from the current settings.
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
			BucketConfig defaultBucket = s.GetDefaultBucket() ?? new BucketConfig() { name = s.defaultBucketName };
			int maxTtl = s.buckets.Where(b => b.enabled).Select(b => b.maxTtlSeconds).DefaultIfEmpty(defaultBucket.maxTtlSeconds).Max();
			Dictionary<string, string> values = new Dictionary<string, string>()
			{
				["operatorName"] = string.IsNullOrWhiteSpace(s.operatorName) ? "the operator of this service" : s.operatorName.Trim(),
				["maxTtl"] = FormatDuration(maxTtl),
				["keyFormatNote"] = s.permissiveKeys ? "This server also accepts keys of " + KvNames.PermissiveKeyMinLength + " to " + KvNames.PermissiveKeyMaxLength + " characters made of letters, digits, underscores, periods, and hyphens.  Those keys are case-sensitive." : "",
				["defaultBucket"] = defaultBucket.name,
				["defaultMaxItemSize"] = StringUtil.FormatDiskBytes(defaultBucket.maxItemSizeBytes),
				["defaultTtl"] = FormatDuration(defaultBucket.defaultTtlSeconds),
				["defaultMaxTtl"] = FormatDuration(defaultBucket.maxTtlSeconds),
				["writeLimit"] = FormatRequestLimit(s.rateLimits.writes),
				["readLimit"] = FormatRequestLimit(s.rateLimits.reads),
				["byteLimit"] = StringUtil.FormatDiskBytes((long)s.rateLimits.bytes.capacity) + " at once, then " + StringUtil.FormatDiskBytes((long)(s.rateLimits.bytes.refillRate * 86400)) + " more per day"
			};
			StringBuilder sb = new StringBuilder(EmbeddedFiles.PublicSite.GetText("index.html"));
			foreach (KeyValuePair<string, string> v in values)
				sb.Replace("{{" + v.Key + "}}", WebUtility.HtmlEncode(v.Value));
			return sb.ToString();
		}
		private static string FormatRequestLimit(TokenBucketSettings t)
		{
			return FormatNumber(t.capacity) + " requests at once, then " + FormatNumber(t.refillRate * 3600) + " more per hour";
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
