using BPUtil;
using BPUtil.MVC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KVStore.Controllers
{
	/// <summary>
	/// <para>Health and usage statistics for the Admin Console dashboard.</para>
	/// <para>By design, nothing here (or anywhere in the Admin Console) returns a key, a value, or a client IP address.  The Admin Console is a health and configuration console, not a data browser.</para>
	/// </summary>
	public class Dashboard : AdminConsoleControllerBase
	{
		public ActionResult Get()
		{
			try
			{
				KvEngine engine = KVStoreService.Engine;
				Settings s = KVStoreService.MakeLocalSettingsReference();
				KvStorage storage = engine.Storage;

				Dictionary<string, BucketItemStats> itemStats = engine.GetItemStats(out long itemStatsAgeMs);
				BucketItemStats globalStats = new BucketItemStats();
				foreach (BucketItemStats bs in itemStats.Values)
					globalStats.Add(bs);

				long globalCount = 0;
				long globalBytes = 0;
				long globalQuotaCount = 0;
				long globalQuotaBytes = 0;
				var buckets = new List<object>();
				foreach (BucketConfig b in s.buckets)
				{
					BucketUsageSnapshot u = storage.GetUsage(b.name);
					globalCount += u.Count;
					globalBytes += u.Bytes;
					globalQuotaCount += b.maxItemCount;
					globalQuotaBytes += b.maxTotalBytes;
					itemStats.TryGetValue(b.name, out BucketItemStats bs);
					buckets.Add(new
					{
						name = b.name,
						enabled = b.enabled,
						isDefault = b.name == s.defaultBucketName,
						itemCount = u.Count,
						bytes = u.Bytes,
						maxItemCount = b.maxItemCount,
						maxTotalBytes = b.maxTotalBytes,
						stats = bs ?? new BucketItemStats()
					});
				}
				// Items stored under bucket names that are no longer configured (e.g. a bucket deletion interrupted by a crash).  They are unreachable and will expire.
				string[] unconfigured = storage.GetBucketsWithItems().Where(name => s.GetBucket(name) == null).ToArray();
				foreach (string name in unconfigured)
				{
					BucketUsageSnapshot u = storage.GetUsage(name);
					globalCount += u.Count;
					globalBytes += u.Bytes;
				}

				Process me = Process.GetCurrentProcess();
				GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
				OrphanSweepResult orphans = storage.LastOrphanSweep;
				RateLimiterSet rl = engine.RateLimits;
				long now = engine.Now();

				return Json(new
				{
					success = true,
					version = Globals.AssemblyVersion,
					serverTimeUnix = now,
					uptimeSeconds = (long)engine.Uptime.TotalSeconds,
					recovery = new
					{
						count = storage.RecoveryEventCount,
						lastUnix = storage.LastRecoveryUnixSeconds,
						lastReason = storage.LastRecoveryReason
					},
					process = new
					{
						managedHeapBytes = GC.GetTotalMemory(false),
						gcHeapSizeBytes = gcInfo.HeapSizeBytes,
						gen0Collections = GC.CollectionCount(0),
						gen1Collections = GC.CollectionCount(1),
						gen2Collections = GC.CollectionCount(2),
						threadCount = me.Threads.Count,
						workingSetBytes = me.WorkingSet64,
						diskFreeBytes = storage.GetFreeDiskBytes(),
						databaseFileBytes = storage.GetDatabaseFileBytes(),
						blobTreeFiles = orphans?.blobTreeFiles,
						blobTreeBytes = orphans?.blobTreeBytes,
						lastOrphanSweepUnix = orphans?.completedUnixSeconds
					},
					rateLimiters = new object[]
					{
						DescribeLimiter("Writes (put, del)", rl.Writes),
						DescribeLimiter("Reads (get, info)", rl.Reads),
						DescribeLimiter("Bytes (put)", rl.Bytes)
					},
					counters = engine.Stats.GetSnapshot(),
					itemStatsAgeMs,
					global = new
					{
						itemCount = globalCount,
						bytes = globalBytes,
						maxItemCount = globalQuotaCount,
						maxTotalBytes = globalQuotaBytes,
						stats = globalStats
					},
					buckets,
					unconfiguredBucketsWithItems = unconfigured,
					sizeHistogramLabels = BucketItemStats.SizeHistogramLabels,
					expiryHistogramLabels = BucketItemStats.ExpiryHistogramLabels
				});
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		private static object DescribeLimiter(string name, ClientRateLimiter limiter)
		{
			return new
			{
				name,
				trackedClients = limiter.NumberOfBuckets,
				maxTrackedClients = limiter.MaxBuckets,
				capacity = limiter.Capacity,
				refillRate = limiter.RefillRate
			};
		}
	}
}
