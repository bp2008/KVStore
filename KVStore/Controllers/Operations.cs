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
	/// Maintenance actions and the danger zone.
	/// </summary>
	public class Operations : AdminConsoleControllerBase
	{
		/// <summary>
		/// Runs the expiry sweep (and database checkpoint) now.
		/// </summary>
		public async Task<ActionResult> RunExpirySweep()
		{
			try
			{
				KvEngine engine = KVStoreService.Engine;
				Stopwatch sw = Stopwatch.StartNew();
				long before = engine.Stats.GetSnapshot()[nameof(OpCounter.ExpiredSwept)].total;
				await Task.Run(engine.RunExpirySweep).ConfigureAwait(false);
				long swept = engine.Stats.GetSnapshot()[nameof(OpCounter.ExpiredSwept)].total - before;
				engine.InvalidateItemStats();
				return Json(new { success = true, message = "Expiry sweep finished in " + sw.ElapsedMilliseconds + " ms.  Expired items deleted: " + swept + "." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Runs the orphan sweep now.
		/// </summary>
		public async Task<ActionResult> RunOrphanSweep()
		{
			try
			{
				KvEngine engine = KVStoreService.Engine;
				Stopwatch sw = Stopwatch.StartNew();
				OrphanSweepResult result = await Task.Run(engine.RunOrphanSweep).ConfigureAwait(false);
				engine.InvalidateItemStats();
				return Json(new { success = true, message = "Orphan sweep finished in " + sw.ElapsedMilliseconds + " ms.  Orphaned files deleted: " + result.filesReclaimed + ".  Metadata records without files deleted: " + result.metadataRemoved + "." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes every item in one bucket.
		/// </summary>
		public async Task<ActionResult> FlushBucket()
		{
			try
			{
				NameRequest request = await ParseRequest<NameRequest>(CancellationToken).ConfigureAwait(false);
				if (!KvNames.TryNormalizeBucketName(request?.name, out string name))
					return ApiError("Invalid bucket name.");
				KvEngine engine = KVStoreService.Engine;
				int deleted = await Task.Run(() => engine.Storage.FlushBucket(name)).ConfigureAwait(false);
				engine.InvalidateItemStats();
				return Json(new { success = true, message = "Deleted " + deleted + " item(s) from bucket \"" + name + "\"." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes every item in every bucket.
		/// </summary>
		public async Task<ActionResult> FlushAll()
		{
			try
			{
				KvEngine engine = KVStoreService.Engine;
				int deleted = await Task.Run(() => engine.Storage.FlushAll()).ConfigureAwait(false);
				engine.InvalidateItemStats();
				return Json(new { success = true, message = "Deleted " + deleted + " item(s) from all buckets." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Rebuilds the database file to reclaim unused space.  Other storage operations wait until it is finished.
		/// </summary>
		public async Task<ActionResult> RebuildDatabase()
		{
			try
			{
				KvEngine engine = KVStoreService.Engine;
				Stopwatch sw = Stopwatch.StartNew();
				long before = engine.Storage.GetDatabaseFileBytes();
				await Task.Run(() => engine.Storage.Rebuild()).ConfigureAwait(false);
				long after = engine.Storage.GetDatabaseFileBytes();
				return Json(new { success = true, message = "Database rebuilt in " + sw.ElapsedMilliseconds + " ms.  Size before: " + StringUtil.FormatDiskBytes(before) + ".  Size after: " + StringUtil.FormatDiskBytes(after) + "." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes one item identified by bucket and key, for takedown requests.  Only reports whether an item was deleted; nothing about the item is revealed.
		/// </summary>
		public async Task<ActionResult> DeleteItem()
		{
			try
			{
				DeleteItemRequest request = await ParseRequest<DeleteItemRequest>(CancellationToken).ConfigureAwait(false);
				string result = DeleteItemInternal(request);
				return Json(new { success = true, message = result });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes one item.  Returns a message describing the result.  Throws if the request is invalid.
		/// </summary>
		/// <param name="request">Bucket and key of the item.</param>
		/// <returns></returns>
		internal static string DeleteItemInternal(DeleteItemRequest request)
		{
			Settings s = KVStoreService.MakeLocalSettingsReference();
			string bucket;
			if (string.IsNullOrEmpty(request?.bucket))
				bucket = s.defaultBucketName;
			else if (!KvNames.TryNormalizeBucketName(request.bucket, out bucket))
				throw new Exception("Invalid bucket name.");
			// Permissive format is always accepted here, so that items stored while permissive mode was enabled can still be taken down.
			if (!KvNames.TryNormalizeKey(request?.key?.Trim(), true, out string key))
				throw new Exception("Invalid key.");
			KvEngine engine = KVStoreService.Engine;
			bool deleted = engine.Storage.Delete(bucket, key);
			engine.InvalidateItemStats();
			return deleted ? "The item was deleted." : "No live item exists with that bucket and key (any expired copy or orphaned file was deleted).";
		}
	}
	public class DeleteItemRequest
	{
		public string bucket;
		public string key;
	}
}
