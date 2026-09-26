using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Settings for the background maintenance tasks.
	/// </summary>
	public class MaintenanceSettings
	{
		/// <summary>
		/// Interval between sweeps that delete expired items, in seconds.  The database log is checkpointed on the same interval.  Expired items are never served, regardless of whether they have been swept yet.
		/// </summary>
		public int expirySweepIntervalSeconds = 60;
		/// <summary>
		/// Interval between orphan sweeps, in seconds.  The orphan sweep deletes value files that have no metadata, metadata whose value file is missing, and abandoned temporary files.
		/// </summary>
		public int orphanSweepIntervalSeconds = 600;
		/// <summary>
		/// Files younger than this many seconds are never deleted by the orphan sweep, to avoid racing an upload that is in progress.
		/// </summary>
		public int orphanMinAgeSeconds = 600;
		/// <summary>
		/// Uploads are refused with "503 storage_full" if storing them would leave less than this many bytes free on the data volume.
		/// </summary>
		public long minFreeDiskBytes = 256L * 1024 * 1024;

		/// <summary>
		/// Validates and repairs values so that they are within sane ranges.
		/// </summary>
		public void Repair()
		{
			expirySweepIntervalSeconds = Math.Clamp(expirySweepIntervalSeconds, 5, 3600);
			orphanSweepIntervalSeconds = Math.Clamp(orphanSweepIntervalSeconds, 60, 86400);
			orphanMinAgeSeconds = Math.Clamp(orphanMinAgeSeconds, 60, 86400);
			if (minFreeDiskBytes < 0)
				minFreeDiskBytes = 0;
		}
	}
}
