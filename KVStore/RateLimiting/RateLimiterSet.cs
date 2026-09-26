using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// An immutable set of the public API's rate limiters.  To apply a configuration change, a new RateLimiterSet is constructed and swapped in, which resets every client's buckets to full.
	/// </summary>
	public class RateLimiterSet
	{
		/// <summary>
		/// Consumed by "put" and "del".
		/// </summary>
		public readonly ClientRateLimiter Writes;
		/// <summary>
		/// Consumed by "get" and "info".
		/// </summary>
		public readonly ClientRateLimiter Reads;
		/// <summary>
		/// Consumed by "put" (1 token per stored byte).
		/// </summary>
		public readonly ClientRateLimiter Bytes;
		/// <summary>
		/// A copy of the settings these rate limiters were built from.
		/// </summary>
		public readonly RateLimitSettings Settings;

		/// <summary>
		/// Constructs a RateLimiterSet from settings.
		/// </summary>
		/// <param name="settings">Rate limit settings.  A copy is retained.</param>
		public RateLimiterSet(RateLimitSettings settings)
		{
			Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<RateLimitSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
			Writes = new ClientRateLimiter(Settings.writes, Settings.maxTrackedClients);
			Reads = new ClientRateLimiter(Settings.reads, Settings.maxTrackedClients);
			Bytes = new ClientRateLimiter(Settings.bytes, Settings.maxTrackedClients);
		}
		/// <summary>
		/// Removes full buckets from all rate limiters.
		/// </summary>
		public void RunMaintenance()
		{
			Writes.RunMaintenance();
			Reads.RunMaintenance();
			Bytes.RunMaintenance();
		}
	}
}
