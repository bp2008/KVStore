using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace KVStore
{
	/// <summary>
	/// Helpers for handling client IP addresses.  Client IP addresses are never written to disk; they exist only as ephemeral keys inside the in-memory rate limiters.
	/// </summary>
	public static class ClientIp
	{
		/// <summary>
		/// <para>Returns the string which identifies a client for rate limiting purposes.</para>
		/// <para>IPv4 addresses (including IPv4-mapped IPv6 addresses) are identified by the address itself.</para>
		/// <para>IPv6 addresses are truncated to their /64 prefix, because a /64 is routinely assigned to a single customer, so limiting per IPv6 address would be trivially bypassed.</para>
		/// </summary>
		/// <param name="address">The client's IP address.</param>
		/// <returns></returns>
		public static string GetRateLimitKey(IPAddress address)
		{
			if (address == null)
				return "unknown";
			if (address.IsIPv4MappedToIPv6)
				address = address.MapToIPv4();
			if (address.AddressFamily == AddressFamily.InterNetworkV6)
			{
				byte[] bytes = address.GetAddressBytes();
				for (int i = 8; i < 16; i++)
					bytes[i] = 0;
				return new IPAddress(bytes).ToString() + "/64";
			}
			return address.ToString();
		}
	}
	/// <summary>
	/// Removes IP addresses from text before it is written to a log file.
	/// </summary>
	public static class IpScrubber
	{
		/// <summary>
		/// The text which replaces each IP address.
		/// </summary>
		public const string Replacement = "[ip-removed]";
		private static readonly Regex rxIPv4 = new Regex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		/// <summary>
		/// Candidate IPv6 addresses: runs of hex digits, colons, and dots containing at least two colons, optionally with a zone ID.
		/// </summary>
		private static readonly Regex rxIPv6Candidate = new Regex(@"(?<![\w:.])[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*(?:%[\w.]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		/// <summary>
		/// Returns the text with all IPv4 and IPv6 addresses replaced by <see cref="Replacement"/>.
		/// </summary>
		/// <param name="text">Text which may contain IP addresses.</param>
		/// <returns></returns>
		public static string Scrub(string text)
		{
			if (string.IsNullOrEmpty(text))
				return text;
			// IPv6 first, because IPv4-mapped IPv6 addresses (e.g. "::ffff:1.2.3.4") contain an IPv4 address.
			text = rxIPv6Candidate.Replace(text, m =>
			{
				// The match may include trailing punctuation (e.g. "addr:" or "addr."), but an address can also legitimately end with "::", so try progressively trimmed candidates.
				foreach (string candidate in new string[] { m.Value, m.Value.TrimEnd('.'), m.Value.TrimEnd('.', ':') })
				{
					if (candidate.Length >= 2 && IPAddress.TryParse(candidate, out IPAddress addr) && addr.AddressFamily == AddressFamily.InterNetworkV6)
						return Replacement + m.Value.Substring(candidate.Length);
				}
				return m.Value;
			});
			text = rxIPv4.Replace(text, m =>
			{
				// .NET assembly versions (e.g. "Version=1.0.0.0") look exactly like IPv4 addresses.
				int start = m.Index;
				if (start >= 8 && string.CompareOrdinal(text, start - 8, "Version=", 0, 8) == 0)
					return m.Value;
				return Replacement;
			});
			return text;
		}
	}
}
