using BPUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace KVStore
{
	/// <summary>
	/// The core of the service, shared by the public API and the Admin Console: storage, rate limiters, counters, and background maintenance.
	/// </summary>
	public class KvEngine : IDisposable
	{
		/// <summary>
		/// Returns the current settings.  The returned object must be treated as read-only.
		/// </summary>
		public readonly Func<Settings> GetSettings;
		/// <summary>
		/// Operation counters.
		/// </summary>
		public readonly OpStats Stats;
		/// <summary>
		/// Storage repository.
		/// </summary>
		public readonly KvStorage Storage;
		/// <summary>
		/// Rate limiter for failed Admin Console authentication attempts.
		/// </summary>
		public readonly AdminAuthLimiter AdminAuthLimiter = new AdminAuthLimiter();
		/// <summary>
		/// Time the engine was constructed, in seconds since the unix epoch.
		/// </summary>
		public readonly long StartedUnixSeconds;
		private readonly Stopwatch uptime = Stopwatch.StartNew();
		private RateLimiterSet rateLimits;
		private long clockOffsetSeconds = 0;
		private MaintenanceWorker maintenance;
		private readonly object itemStatsLock = new object();
		private Dictionary<string, BucketItemStats> cachedItemStats = null;
		private long cachedItemStatsAtMs = long.MinValue;
		/// <summary>
		/// How long computed item statistics are reused, in milliseconds.
		/// </summary>
		public const long ItemStatsCacheMs = 10000;

		/// <summary>
		/// Gets the current set of public API rate limiters.  The set is replaced (not modified) when the rate limit settings change.
		/// </summary>
		public RateLimiterSet RateLimits => Volatile.Read(ref rateLimits);
		/// <summary>
		/// Gets the time since the engine was constructed.
		/// </summary>
		public TimeSpan Uptime => uptime.Elapsed;

		/// <summary>
		/// Constructs a KvEngine.  Call <see cref="Start"/> before use.
		/// </summary>
		/// <param name="dataDirectory">Root data directory.</param>
		/// <param name="getSettings">Returns the current settings.</param>
		public KvEngine(string dataDirectory, Func<Settings> getSettings)
		{
			GetSettings = getSettings;
			StartedUnixSeconds = Now();
			Stats = new OpStats(Now);
			Storage = new KvStorage(dataDirectory, Stats, Now);
			rateLimits = new RateLimiterSet(getSettings().rateLimits);
		}
		/// <summary>
		/// Opens storage (recovering it if necessary) and starts background maintenance.
		/// </summary>
		/// <param name="startMaintenance">If false, background maintenance is not started (for tests, which run maintenance explicitly).</param>
		public void Start(bool startMaintenance = true)
		{
			Storage.Open();
			if (startMaintenance)
			{
				maintenance = new MaintenanceWorker(this);
				maintenance.Start();
			}
		}
		/// <summary>
		/// Returns the current time in seconds since the unix epoch.
		/// </summary>
		/// <returns></returns>
		public long Now()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref clockOffsetSeconds);
		}
		/// <summary>
		/// Moves the engine's clock forward.  For tests only.
		/// </summary>
		/// <param name="seconds">Number of seconds to add.</param>
		public void AdvanceClockForTesting(long seconds)
		{
			Interlocked.Add(ref clockOffsetSeconds, seconds);
		}
		/// <summary>
		/// Applies settings that require action beyond being read on each request.  Currently: if the rate limit settings changed, the rate limiters are replaced, which resets every client's buckets to full.
		/// </summary>
		/// <param name="s">The new settings.</param>
		public void ApplySettings(Settings s)
		{
			RateLimiterSet current = RateLimits;
			if (!current.Settings.ValueEquals(s.rateLimits))
				Volatile.Write(ref rateLimits, new RateLimiterSet(s.rateLimits));
		}
		/// <summary>
		/// Runs the expiry sweep, checkpoints the database, and performs rate limiter housekeeping.
		/// </summary>
		public void RunExpirySweep()
		{
			Storage.RetryRecoveryIfUnavailable();
			Storage.SweepExpired();
			Storage.Checkpoint();
			RateLimits.RunMaintenance();
			AdminAuthLimiter.RunMaintenance();
		}
		/// <summary>
		/// Runs the orphan sweep.
		/// </summary>
		/// <returns></returns>
		public OrphanSweepResult RunOrphanSweep()
		{
			return Storage.SweepOrphans(GetSettings().maintenance.orphanMinAgeSeconds);
		}
		/// <summary>
		/// Returns per-bucket item statistics, computed by a full scan of the metadata at most once per <see cref="ItemStatsCacheMs"/>.
		/// </summary>
		/// <param name="computedAgoMs">(Output) How long ago the statistics were computed, in milliseconds.</param>
		/// <returns></returns>
		public Dictionary<string, BucketItemStats> GetItemStats(out long computedAgoMs)
		{
			lock (itemStatsLock)
			{
				long nowMs = uptime.ElapsedMilliseconds;
				if (cachedItemStats == null || nowMs - cachedItemStatsAtMs >= ItemStatsCacheMs)
				{
					cachedItemStats = Storage.ComputeItemStats();
					cachedItemStatsAtMs = nowMs;
				}
				computedAgoMs = nowMs - cachedItemStatsAtMs;
				return cachedItemStats;
			}
		}
		/// <summary>
		/// Discards cached item statistics so the next request recomputes them.
		/// </summary>
		public void InvalidateItemStats()
		{
			lock (itemStatsLock)
			{
				cachedItemStats = null;
			}
		}
		/// <summary>
		/// Stops background maintenance and closes storage.
		/// </summary>
		public void Dispose()
		{
			maintenance?.Stop();
			Storage.Dispose();
		}
	}
}
