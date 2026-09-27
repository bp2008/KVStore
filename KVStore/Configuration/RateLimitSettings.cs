using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Per-client rate limits for the public API.  Clients are identified by IP address (IPv4) or /64 prefix (IPv6).  Applying a change replaces the rate limiters, which resets every client's bucket to full.
	/// </summary>
	public class RateLimitSettings
	{
		/// <summary>
		/// Consumed by "put", "putraw", and "del" (1 token per request).  Default: 10 tokens, refilled at 60 per hour.
		/// </summary>
		public TokenBucketSettings writes = new TokenBucketSettings(10, 60.0 / 3600);
		/// <summary>
		/// Consumed by "get", "getraw", and "info" (1 token per request).  Default: 30 tokens, refilled at 600 per hour.
		/// </summary>
		public TokenBucketSettings reads = new TokenBucketSettings(30, 600.0 / 3600);
		/// <summary>
		/// Consumed by "put" and "putraw" (1 token per stored byte).  Default: 10 MiB, refilled at 60 MiB per hour (a full refill takes 10 minutes).
		/// </summary>
		public TokenBucketSettings bytes = new TokenBucketSettings(10485760, 10485760.0 * 6 / 3600);
		/// <summary>
		/// Maximum number of clients that each rate limiter will track at once.  When a rate limiter is tracking this many clients, requests from clients it is not already tracking are refused with "429 rate_limited" rather than allocating more memory.
		/// </summary>
		public int maxTrackedClients = 250000;

		/// <summary>
		/// Validates and repairs values so that they are within sane ranges.
		/// </summary>
		public void Repair()
		{
			if (writes == null)
				writes = new TokenBucketSettings(10, 60.0 / 3600);
			if (reads == null)
				reads = new TokenBucketSettings(30, 600.0 / 3600);
			if (bytes == null)
				bytes = new TokenBucketSettings(10485760, 10485760.0 * 6 / 3600);
			writes.Repair();
			reads.Repair();
			bytes.Repair();
			if (maxTrackedClients < 1000)
				maxTrackedClients = 1000;
		}
		/// <summary>
		/// Returns true if this instance has the same values as another instance.
		/// </summary>
		/// <param name="other">Other instance.</param>
		/// <returns></returns>
		public bool ValueEquals(RateLimitSettings other)
		{
			return other != null
				&& writes.ValueEquals(other.writes)
				&& reads.ValueEquals(other.reads)
				&& bytes.ValueEquals(other.bytes)
				&& maxTrackedClients == other.maxTrackedClients;
		}
	}
	/// <summary>
	/// Configuration of one token bucket rate limiter.
	/// </summary>
	public class TokenBucketSettings
	{
		/// <summary>
		/// The maximum number of tokens a client's bucket can hold.  Buckets start full.
		/// </summary>
		public double capacity;
		/// <summary>
		/// The rate at which tokens are added to a client's bucket, in tokens per second.
		/// </summary>
		public double refillRate;

		/// <summary>
		/// Constructs an empty TokenBucketSettings (for deserialization).
		/// </summary>
		public TokenBucketSettings() { }
		/// <summary>
		/// Constructs a TokenBucketSettings.
		/// </summary>
		/// <param name="capacity">The maximum number of tokens a client's bucket can hold.</param>
		/// <param name="refillRate">The rate at which tokens are added to a client's bucket, in tokens per second.</param>
		public TokenBucketSettings(double capacity, double refillRate)
		{
			this.capacity = capacity;
			this.refillRate = refillRate;
		}
		/// <summary>
		/// Validates and repairs values so that they are positive numbers.
		/// </summary>
		public void Repair()
		{
			if (double.IsNaN(capacity) || double.IsInfinity(capacity) || capacity < 1)
				capacity = 1;
			if (double.IsNaN(refillRate) || double.IsInfinity(refillRate) || refillRate <= 0)
				refillRate = 0.0001;
		}
		/// <summary>
		/// Returns true if this instance has the same values as another instance.
		/// </summary>
		/// <param name="other">Other instance.</param>
		/// <returns></returns>
		public bool ValueEquals(TokenBucketSettings other)
		{
			return other != null && capacity == other.capacity && refillRate == other.refillRate;
		}
	}
}
