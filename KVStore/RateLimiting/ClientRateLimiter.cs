using BPUtil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>A <see cref="TokenBucketDictionary{TKey}"/> keyed on client identity, with a ceiling on the number of clients it will track.</para>
	/// <para><see cref="TokenBucketDictionary{TKey}"/> only removes a client's bucket once it refills to capacity, so a distributed flood could otherwise grow the dictionary without bound.  When this limiter is tracking <see cref="MaxBuckets"/> clients, requests from clients it is not already tracking are refused instead of allocating a new bucket.</para>
	/// <para>Capacity and refill rate are fixed at construction.  To change them, construct a replacement limiter (which resets every client's bucket to full).</para>
	/// </summary>
	public class ClientRateLimiter : TokenBucketDictionary<string>
	{
		/// <summary>
		/// Interval between removals of full buckets, in milliseconds.
		/// </summary>
		public const double MaintenanceIntervalMs = 30000;
		/// <summary>
		/// Retry-After value (seconds) returned when the limiter refuses a new client because it is tracking too many clients.
		/// </summary>
		public const int TableFullRetryAfterSeconds = 30;
		/// <summary>
		/// Maximum number of clients this limiter will track at once.
		/// </summary>
		public int MaxBuckets { get; private set; }

		/// <summary>
		/// Constructs a ClientRateLimiter.
		/// </summary>
		/// <param name="capacity">The maximum number of tokens that buckets can hold.  Buckets start full.</param>
		/// <param name="refillRate">The rate at which tokens are added to buckets (tokens per second).</param>
		/// <param name="maxBuckets">Maximum number of clients this limiter will track at once.</param>
		public ClientRateLimiter(double capacity, double refillRate, int maxBuckets) : base(capacity, refillRate, MaintenanceIntervalMs)
		{
			MaxBuckets = maxBuckets;
		}
		/// <summary>
		/// Constructs a ClientRateLimiter from settings.
		/// </summary>
		/// <param name="s">Token bucket settings.</param>
		/// <param name="maxBuckets">Maximum number of clients this limiter will track at once.</param>
		public ClientRateLimiter(TokenBucketSettings s, int maxBuckets) : this(s.capacity, s.refillRate, maxBuckets) { }

		/// <summary>
		/// Attempts to consume tokens from the client's bucket.  Returns 0 if successful, otherwise the number of seconds the client should wait before retrying (at least 1).
		/// </summary>
		/// <param name="clientKey">Client identity (see <see cref="ClientIp.GetRateLimitKey"/>).</param>
		/// <param name="tokens">Number of tokens to consume.  Must be positive.</param>
		/// <returns>0 if the tokens were consumed, otherwise a Retry-After value in seconds.</returns>
		public int TryConsumeOrGetRetryAfter(string clientKey, double tokens)
		{
			if (tokens <= 0)
				throw new ArgumentOutOfRangeException(nameof(tokens), tokens, "must be a positive number");
			lock (_lock)
			{
				Maintain();
				if (!_buckets.TryGetValue(clientKey, out TokenBucket bucket))
				{
					if (tokens > Capacity)
						return GetRetryAfter(tokens, Capacity);
					if (_buckets.Count >= MaxBuckets)
						return TableFullRetryAfterSeconds;
					bucket = _buckets[clientKey] = CreateNewBucket(clientKey);
				}
				if (bucket.TryConsume(tokens))
					return 0;
				return GetRetryAfter(tokens, bucket.Peek());
			}
		}
		/// <summary>
		/// Checks whether the client's bucket currently holds at least the given number of tokens, without consuming any and without allocating a bucket.  Returns 0 if so, otherwise the number of seconds the client should wait before retrying (at least 1).
		/// </summary>
		/// <param name="clientKey">Client identity (see <see cref="ClientIp.GetRateLimitKey"/>).</param>
		/// <param name="tokens">Number of tokens required.</param>
		/// <returns>0 if the tokens are available, otherwise a Retry-After value in seconds.</returns>
		public int CheckAvailable(string clientKey, double tokens)
		{
			if (tokens <= 0)
				return 0;
			double available;
			lock (_lock)
			{
				Maintain();
				if (_buckets.TryGetValue(clientKey, out TokenBucket bucket))
					available = bucket.Peek();
				else if (_buckets.Count >= MaxBuckets)
					return TableFullRetryAfterSeconds;
				else
					available = Capacity;
			}
			if (available >= tokens)
				return 0;
			return GetRetryAfter(tokens, available);
		}
		/// <summary>
		/// Removes full buckets now, regardless of the maintenance interval.  Call periodically so that <see cref="TokenBucketDictionary{TKey}.NumberOfBuckets"/> stays accurate while there is no traffic.
		/// </summary>
		public void RunMaintenance()
		{
			lock (_lock)
			{
				RemoveFullBuckets();
			}
		}
		private int GetRetryAfter(double tokensNeeded, double tokensAvailable)
		{
			double seconds = Math.Ceiling((tokensNeeded - tokensAvailable) / RefillRate);
			if (double.IsNaN(seconds) || seconds < 1)
				return 1;
			if (seconds > int.MaxValue)
				return int.MaxValue;
			return (int)seconds;
		}
	}
}
