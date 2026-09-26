using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>Rate limits failed Admin Console authentication attempts per client address: 5 failures per minute, then exponential backoff.</para>
	/// <para>Each failure consumes a token from a <see cref="ClientRateLimiter"/> (capacity 5, refilled at 5 per minute).  A failure with no token available locks the client out for 2^n seconds, where n is the number of such failures since the client's last successful login (capped at <see cref="MaxLockoutSeconds"/>).  While locked out, even correct credentials are refused.</para>
	/// </summary>
	public class AdminAuthLimiter
	{
		/// <summary>
		/// Maximum lockout duration in seconds.
		/// </summary>
		public const int MaxLockoutSeconds = 900;
		/// <summary>
		/// Maximum number of clients with backoff state.  This only bounds memory use; the Admin Console is reachable only over a VPN or loopback, so it is not expected to be reached.
		/// </summary>
		private const int MaxTrackedClients = 10000;
		private readonly ClientRateLimiter failures = new ClientRateLimiter(5, 5.0 / 60, MaxTrackedClients);
		private readonly Dictionary<string, Backoff> backoffs = new Dictionary<string, Backoff>();
		private readonly Stopwatch clock = Stopwatch.StartNew();
		private class Backoff
		{
			public int Strikes;
			public long LockedUntilMs;
		}
		/// <summary>
		/// Returns 0 if the client may attempt to authenticate, otherwise the number of seconds until it may try again.
		/// </summary>
		/// <param name="clientKey">Client identity.</param>
		/// <returns></returns>
		public int GetLockoutSeconds(string clientKey)
		{
			lock (backoffs)
			{
				if (backoffs.TryGetValue(clientKey, out Backoff b))
				{
					long remainingMs = b.LockedUntilMs - clock.ElapsedMilliseconds;
					if (remainingMs > 0)
						return (int)Math.Ceiling(remainingMs / 1000.0);
				}
			}
			return 0;
		}
		/// <summary>
		/// Records a failed authentication attempt.
		/// </summary>
		/// <param name="clientKey">Client identity.</param>
		public void RecordFailure(string clientKey)
		{
			if (failures.TryConsumeOrGetRetryAfter(clientKey, 1) == 0)
				return;
			lock (backoffs)
			{
				if (!backoffs.TryGetValue(clientKey, out Backoff b))
				{
					if (backoffs.Count >= MaxTrackedClients)
						RemoveExpired();
					if (backoffs.Count >= MaxTrackedClients)
						return;
					b = backoffs[clientKey] = new Backoff();
				}
				b.Strikes = Math.Min(b.Strikes + 1, 30);
				long lockoutMs = Math.Min((1L << b.Strikes) * 1000, MaxLockoutSeconds * 1000L);
				b.LockedUntilMs = clock.ElapsedMilliseconds + lockoutMs;
			}
		}
		/// <summary>
		/// Records a successful authentication, which clears the client's backoff state.
		/// </summary>
		/// <param name="clientKey">Client identity.</param>
		public void RecordSuccess(string clientKey)
		{
			lock (backoffs)
			{
				backoffs.Remove(clientKey);
			}
		}
		/// <summary>
		/// Forgets clients whose lockout ended more than an hour ago.
		/// </summary>
		public void RunMaintenance()
		{
			failures.RunMaintenance();
			lock (backoffs)
			{
				RemoveExpired();
			}
		}
		/// <summary>
		/// Hold the lock when calling this.
		/// </summary>
		private void RemoveExpired()
		{
			long cutoff = clock.ElapsedMilliseconds - 3600000;
			foreach (string key in backoffs.Where(kvp => kvp.Value.LockedUntilMs < cutoff).Select(kvp => kvp.Key).ToList())
				backoffs.Remove(key);
		}
	}
}
