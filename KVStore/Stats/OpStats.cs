using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace KVStore
{
	/// <summary>
	/// Aggregate operation counters.  There is deliberately no per-client dimension.
	/// </summary>
	public enum OpCounter
	{
		/// <summary>Total requests to the public API.</summary>
		Requests,
		/// <summary>"put" requests.</summary>
		Puts,
		/// <summary>"get" requests.</summary>
		Gets,
		/// <summary>"del" requests.</summary>
		Dels,
		/// <summary>"info" requests.</summary>
		Infos,
		/// <summary>"buckets" requests.</summary>
		BucketLists,
		/// <summary>"phrase" requests.</summary>
		Phrases,
		/// <summary>"health" requests.</summary>
		Healths,
		/// <summary>Responses with status 400.</summary>
		Status400,
		/// <summary>Responses with status 404.</summary>
		Status404,
		/// <summary>Responses with status 413.</summary>
		Status413,
		/// <summary>Responses with status 429.</summary>
		Status429,
		/// <summary>Responses with status 503.</summary>
		Status503,
		/// <summary>Responses with status 500 or any other 5xx status besides 503.</summary>
		Status5xx,
		/// <summary>Bytes of request body received.</summary>
		BytesIn,
		/// <summary>Bytes of response body sent.</summary>
		BytesOut,
		/// <summary>Expired items deleted by sweeps.</summary>
		ExpiredSwept,
		/// <summary>Items evicted to make room under a bucket's quota.</summary>
		QuotaEvicted,
		/// <summary>Value files deleted by the orphan sweeper because they had no metadata (includes abandoned temporary files).</summary>
		OrphanFilesReclaimed,
		/// <summary>Metadata records deleted by the orphan sweeper because their value file was missing.</summary>
		OrphanMetadataRemoved,
		/// <summary>Failed Admin Console authentication attempts.</summary>
		AdminAuthFailures
	}
	/// <summary>
	/// Thread-safe operation counters with rolling windows of 1 minute, 1 hour, and 24 hours, plus totals since the service started.
	/// </summary>
	public class OpStats
	{
		private static readonly int counterCount = Enum.GetValues(typeof(OpCounter)).Length;
		/// <summary>
		/// One slot of a ring buffer of counters.
		/// </summary>
		private class Slot
		{
			public long Epoch = long.MinValue;
			public readonly long[] Counts = new long[counterCount];
		}
		private readonly object myLock = new object();
		/// <summary>
		/// 60 one-second slots, for the 1-minute window.
		/// </summary>
		private readonly Slot[] seconds = CreateSlots(60);
		/// <summary>
		/// 1440 one-minute slots, for the 1-hour and 24-hour windows.
		/// </summary>
		private readonly Slot[] minutes = CreateSlots(1440);
		private readonly long[] totals = new long[counterCount];
		private readonly Func<long> getUnixTimeSeconds;

		/// <summary>
		/// Constructs an OpStats.
		/// </summary>
		/// <param name="getUnixTimeSeconds">A function that returns the current time in seconds since the unix epoch.</param>
		public OpStats(Func<long> getUnixTimeSeconds)
		{
			this.getUnixTimeSeconds = getUnixTimeSeconds;
		}
		private static Slot[] CreateSlots(int count)
		{
			Slot[] slots = new Slot[count];
			for (int i = 0; i < count; i++)
				slots[i] = new Slot();
			return slots;
		}
		/// <summary>
		/// Adds the given amount to a counter.
		/// </summary>
		/// <param name="counter">Counter to increment.</param>
		/// <param name="amount">Amount to add.</param>
		public void Add(OpCounter counter, long amount = 1)
		{
			if (amount == 0)
				return;
			long now = getUnixTimeSeconds();
			lock (myLock)
			{
				totals[(int)counter] += amount;
				GetSlot(seconds, now).Counts[(int)counter] += amount;
				GetSlot(minutes, now / 60).Counts[(int)counter] += amount;
			}
		}
		/// <summary>
		/// Returns the slot for the given epoch (second or minute number), resetting it if it holds an older epoch.  Hold the lock when calling this.
		/// </summary>
		private static Slot GetSlot(Slot[] slots, long epoch)
		{
			Slot slot = slots[(int)(epoch % slots.Length)];
			if (slot.Epoch != epoch)
			{
				slot.Epoch = epoch;
				Array.Clear(slot.Counts, 0, slot.Counts.Length);
			}
			return slot;
		}
		/// <summary>
		/// Returns a snapshot of all counters.
		/// </summary>
		/// <returns></returns>
		public Dictionary<string, OpCounterSnapshot> GetSnapshot()
		{
			long now = getUnixTimeSeconds();
			long nowMinute = now / 60;
			long[] last1m = new long[counterCount];
			long[] last1h = new long[counterCount];
			long[] last24h = new long[counterCount];
			long[] total = new long[counterCount];
			lock (myLock)
			{
				Array.Copy(totals, total, counterCount);
				foreach (Slot slot in seconds)
					if (slot.Epoch > now - 60 && slot.Epoch <= now)
						AddArray(last1m, slot.Counts);
				foreach (Slot slot in minutes)
				{
					if (slot.Epoch > nowMinute - 1440 && slot.Epoch <= nowMinute)
					{
						AddArray(last24h, slot.Counts);
						if (slot.Epoch > nowMinute - 60)
							AddArray(last1h, slot.Counts);
					}
				}
			}
			Dictionary<string, OpCounterSnapshot> result = new Dictionary<string, OpCounterSnapshot>();
			foreach (OpCounter c in Enum.GetValues(typeof(OpCounter)))
			{
				int i = (int)c;
				result[c.ToString()] = new OpCounterSnapshot(last1m[i], last1h[i], last24h[i], total[i]);
			}
			return result;
		}
		private static void AddArray(long[] target, long[] source)
		{
			for (int i = 0; i < target.Length; i++)
				target[i] += source[i];
		}
	}
	/// <summary>
	/// The values of one counter over several time windows.
	/// </summary>
	public class OpCounterSnapshot
	{
		/// <summary>Count during the last minute.</summary>
		public long last1m;
		/// <summary>Count during the last hour.</summary>
		public long last1h;
		/// <summary>Count during the last 24 hours.</summary>
		public long last24h;
		/// <summary>Count since the service started.</summary>
		public long total;
		/// <summary>
		/// Constructs an OpCounterSnapshot.
		/// </summary>
		public OpCounterSnapshot(long last1m, long last1h, long last24h, long total)
		{
			this.last1m = last1m;
			this.last1h = last1h;
			this.last24h = last24h;
			this.total = total;
		}
	}
}
