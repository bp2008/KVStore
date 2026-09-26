using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Size and expiration statistics of the items in one bucket (or all buckets combined), computed from a scan of the metadata.  Contains no keys.
	/// </summary>
	public class BucketItemStats
	{
		/// <summary>
		/// Upper bounds (exclusive) of the size histogram's bins, in bytes.  The last bin has no upper bound.
		/// </summary>
		public static readonly long[] SizeHistogramBounds = new long[] { 1024, 16 * 1024, 256 * 1024, 1024 * 1024 };
		/// <summary>
		/// Labels of the size histogram's bins.
		/// </summary>
		public static readonly string[] SizeHistogramLabels = new string[] { "0-1 KiB", "1-16 KiB", "16-256 KiB", "256 KiB-1 MiB", "1 MiB+" };
		/// <summary>
		/// Upper bounds (exclusive) of the time-until-expiration bins, in seconds.  The last bin has no upper bound.
		/// </summary>
		public static readonly long[] ExpiryHistogramBounds = new long[] { 5 * 60, 30 * 60, 60 * 60 };
		/// <summary>
		/// Labels of the time-until-expiration bins.
		/// </summary>
		public static readonly string[] ExpiryHistogramLabels = new string[] { "< 5 min", "5-30 min", "30-60 min", "> 1 h" };

		/// <summary>Number of live (unexpired) items.</summary>
		public long count;
		/// <summary>Total size of live items, in bytes.</summary>
		public long bytes;
		/// <summary>Size of the smallest live item, in bytes (0 if there are none).</summary>
		public long minSize;
		/// <summary>Size of the largest live item, in bytes (0 if there are none).</summary>
		public long maxSize;
		/// <summary>Average size of live items, in bytes (0 if there are none).</summary>
		public double avgSize;
		/// <summary>Number of live items in each size bin (see <see cref="SizeHistogramLabels"/>).</summary>
		public long[] sizeHistogram = new long[SizeHistogramLabels.Length];
		/// <summary>Number of live items in each time-until-expiration bin (see <see cref="ExpiryHistogramLabels"/>).</summary>
		public long[] expiryHistogram = new long[ExpiryHistogramLabels.Length];
		/// <summary>Number of expired items that have not been swept yet.  They are never served.</summary>
		public long expiredPendingSweep;
		/// <summary>Total size of expired items that have not been swept yet, in bytes.</summary>
		public long expiredPendingSweepBytes;

		/// <summary>
		/// Adds an item to the statistics.
		/// </summary>
		/// <param name="m">Item metadata.</param>
		/// <param name="now">Current time in seconds since the unix epoch.</param>
		public void Add(KvMeta m, long now)
		{
			if (m.Expires <= now)
			{
				expiredPendingSweep++;
				expiredPendingSweepBytes += m.Size;
				return;
			}
			if (count == 0 || m.Size < minSize)
				minSize = m.Size;
			if (count == 0 || m.Size > maxSize)
				maxSize = m.Size;
			count++;
			bytes += m.Size;
			avgSize = (double)bytes / count;
			sizeHistogram[GetBin(SizeHistogramBounds, m.Size)]++;
			expiryHistogram[GetBin(ExpiryHistogramBounds, m.Expires - now)]++;
		}
		/// <summary>
		/// Adds another instance's statistics to this one.
		/// </summary>
		/// <param name="other">Statistics to add.</param>
		public void Add(BucketItemStats other)
		{
			if (other.count > 0)
			{
				if (count == 0 || other.minSize < minSize)
					minSize = other.minSize;
				if (count == 0 || other.maxSize > maxSize)
					maxSize = other.maxSize;
			}
			count += other.count;
			bytes += other.bytes;
			avgSize = count == 0 ? 0 : (double)bytes / count;
			for (int i = 0; i < sizeHistogram.Length; i++)
				sizeHistogram[i] += other.sizeHistogram[i];
			for (int i = 0; i < expiryHistogram.Length; i++)
				expiryHistogram[i] += other.expiryHistogram[i];
			expiredPendingSweep += other.expiredPendingSweep;
			expiredPendingSweepBytes += other.expiredPendingSweepBytes;
		}
		private static int GetBin(long[] bounds, long value)
		{
			for (int i = 0; i < bounds.Length; i++)
				if (value < bounds[i])
					return i;
			return bounds.Length;
		}
	}
}
