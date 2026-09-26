using BPUtil;
using BPUtil.SimpleHttp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>The logger used by BPUtil's HTTP servers in KVStore.  It replaces BPUtil's default <see cref="HttpLogger"/>, because BPUtil's error paths include the client's IP address in log messages, and KVStore never writes client IP addresses to disk.</para>
	/// <para>Every message has IP addresses removed before it is logged.  Request logging is not supported and is silently discarded.</para>
	/// </summary>
	public class ScrubbingHttpLogger : ILogger
	{
		/// <summary>
		/// Prefixes of messages that BPUtil logs about its own listening sockets.  These contain only the server's own addresses, so they are logged without scrubbing to keep them useful.
		/// </summary>
		private static readonly string[] listenerMessagePrefixes = new string[] { "Started Listener ", "Resumed Listener ", "Stopped Listener ", "Modified AllowedConnectionTypes on Listener " };
		/// <inheritdoc/>
		public void Log(Exception ex, string additionalInformation = "")
		{
			StringBuilder sb = new StringBuilder();
			if (!string.IsNullOrEmpty(additionalInformation))
				sb.AppendLine(additionalInformation);
			if (ex != null)
				sb.Append(ex.ToHierarchicalString());
			Log(sb.ToString());
		}
		/// <inheritdoc/>
		public void Log(string str)
		{
			if (string.IsNullOrEmpty(str))
				return;
			// KVStore deliberately responds to oversized uploads without reading the request body, which causes BPUtil to report that the body was not read before it closes the connection.  That is expected, and logging it would let any client fill the log.
			if (str.Contains("HttpRequestBodyNotReadException"))
				return;
			foreach (string prefix in listenerMessagePrefixes)
			{
				if (str.StartsWith(prefix, StringComparison.Ordinal) && !str.Contains(Environment.NewLine))
				{
					Logger.Debug(str);
					return;
				}
			}
			Logger.Debug(IpScrubber.Scrub(str));
		}
		/// <summary>
		/// Does nothing.  KVStore never logs requests, because request logs contain client IP addresses.
		/// </summary>
		/// <param name="time">Ignored.</param>
		/// <param name="line">Ignored.</param>
		public void LogRequest(DateTime time, string line)
		{
		}
	}
}
