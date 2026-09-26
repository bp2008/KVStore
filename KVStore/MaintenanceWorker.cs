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
	/// Runs the expiry sweep (which also checkpoints the database) and the orphan sweep on the intervals given in <see cref="MaintenanceSettings"/>.  Interval changes take effect without a restart.
	/// </summary>
	public class MaintenanceWorker
	{
		/// <summary>
		/// Delay before the first orphan sweep, in seconds.  The first sweep happens soon after startup so that the Admin Console has blob tree statistics to show.
		/// </summary>
		private const int firstOrphanSweepDelaySeconds = 60;
		private readonly KvEngine engine;
		private readonly CancellationTokenSource cts = new CancellationTokenSource();
		private Thread thread;

		/// <summary>
		/// Constructs a MaintenanceWorker.
		/// </summary>
		/// <param name="engine">The engine to maintain.</param>
		public MaintenanceWorker(KvEngine engine)
		{
			this.engine = engine;
		}
		/// <summary>
		/// Starts the background thread.
		/// </summary>
		public void Start()
		{
			thread = new Thread(Loop);
			thread.Name = "KVStore Maintenance";
			thread.IsBackground = true;
			thread.Start();
		}
		/// <summary>
		/// Stops the background thread, waiting briefly for a sweep in progress to finish.
		/// </summary>
		public void Stop()
		{
			cts.Cancel();
			thread?.Join(10000);
		}
		private void Loop()
		{
			Stopwatch sw = Stopwatch.StartNew();
			long nextExpirySweepMs = 0;
			long nextOrphanSweepMs = firstOrphanSweepDelaySeconds * 1000L;
			while (!cts.Token.WaitHandle.WaitOne(1000))
			{
				MaintenanceSettings ms = engine.GetSettings().maintenance;
				if (sw.ElapsedMilliseconds >= nextExpirySweepMs)
				{
					Run("expiry sweep", engine.RunExpirySweep);
					nextExpirySweepMs = sw.ElapsedMilliseconds + ms.expirySweepIntervalSeconds * 1000L;
				}
				if (cts.IsCancellationRequested)
					break;
				if (sw.ElapsedMilliseconds >= nextOrphanSweepMs)
				{
					Run("orphan sweep", () => engine.RunOrphanSweep());
					nextOrphanSweepMs = sw.ElapsedMilliseconds + ms.orphanSweepIntervalSeconds * 1000L;
				}
			}
		}
		private void Run(string name, Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				if (!cts.IsCancellationRequested)
					KVStoreService.ReportError(ex, "Maintenance task \"" + name + "\" failed.");
			}
		}
	}
}
