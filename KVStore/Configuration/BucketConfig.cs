using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Configuration of one bucket.  A bucket is a named namespace with its own limits.  Buckets are created and configured only through the Admin Console.
	/// </summary>
	public class BucketConfig
	{
		/// <summary>
		/// Minimum TTL that a client can obtain, in seconds.  Shorter TTL requests are raised to this value.
		/// </summary>
		public const int MinTtlSeconds = 60;
		/// <summary>
		/// Bucket name (1-32 base32 characters, normalized to lower case).
		/// </summary>
		public string name = "default";
		/// <summary>
		/// If false, all operations on this bucket fail with "503 bucket_disabled".
		/// </summary>
		public bool enabled = true;
		/// <summary>
		/// TTL applied when the client omits "ttl", in seconds.
		/// </summary>
		public int defaultTtlSeconds = 3600;
		/// <summary>
		/// Maximum TTL, in seconds.  Client requests above this are clamped.
		/// </summary>
		public int maxTtlSeconds = 7200;
		/// <summary>
		/// Maximum size of one item's value (decoded), in bytes.
		/// </summary>
		public long maxItemSizeBytes = 5242880;
		/// <summary>
		/// Maximum number of items in this bucket.  When a new item would exceed this, the oldest items are evicted.
		/// </summary>
		public int maxItemCount = 100000;
		/// <summary>
		/// Maximum total size of all values in this bucket, in bytes.  When a new item would exceed this, the oldest items are evicted.
		/// </summary>
		public long maxTotalBytes = 1073741824;
		/// <summary>
		/// Free-text notes, visible only in the Admin Console.
		/// </summary>
		public string notes = "";

		/// <summary>
		/// Returns a copy of this bucket configuration.
		/// </summary>
		/// <returns></returns>
		public BucketConfig Clone()
		{
			return (BucketConfig)MemberwiseClone();
		}
		/// <summary>
		/// Returns the TTL (in seconds) that applies to a request for the given TTL.  Null requests the default TTL.  The result is clamped to [<see cref="MinTtlSeconds"/>, <see cref="maxTtlSeconds"/>].
		/// </summary>
		/// <param name="requestedTtl">The TTL requested by the client, or null.</param>
		/// <returns></returns>
		public int ApplyTtl(double? requestedTtl)
		{
			double ttl = requestedTtl ?? defaultTtlSeconds;
			if (double.IsNaN(ttl))
				ttl = defaultTtlSeconds;
			if (ttl > maxTtlSeconds)
				ttl = maxTtlSeconds;
			if (ttl < MinTtlSeconds)
				ttl = MinTtlSeconds;
			return (int)Math.Floor(ttl);
		}
		/// <summary>
		/// Validates and repairs numeric values so that they are within sane ranges.  Does not validate the name.
		/// </summary>
		public void RepairLimits()
		{
			if (maxTtlSeconds < MinTtlSeconds)
				maxTtlSeconds = MinTtlSeconds;
			if (defaultTtlSeconds < MinTtlSeconds)
				defaultTtlSeconds = MinTtlSeconds;
			if (defaultTtlSeconds > maxTtlSeconds)
				defaultTtlSeconds = maxTtlSeconds;
			if (maxItemSizeBytes < 1)
				maxItemSizeBytes = 1;
			// Values are streamed to disk, so there is no memory-based limit, but 1 GiB is far beyond this service's purpose.
			if (maxItemSizeBytes > 1024L * 1024 * 1024)
				maxItemSizeBytes = 1024L * 1024 * 1024;
			if (maxItemCount < 1)
				maxItemCount = 1;
			if (maxTotalBytes < 1)
				maxTotalBytes = 1;
			if (notes == null)
				notes = "";
		}
	}
}
