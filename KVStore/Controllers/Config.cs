using BPUtil;
using BPUtil.MVC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KVStore.Controllers
{
	/// <summary>
	/// Configuration screens of the Admin Console.  Every change is saved to the settings file and takes effect without a restart.
	/// </summary>
	public class Config : AdminConsoleControllerBase
	{
		/// <summary>
		/// Returns the configuration (without the Admin Console password).
		/// </summary>
		public ActionResult Get()
		{
			try
			{
				Settings s = KVStoreService.MakeLocalSettingsReference();
				return Json(new
				{
					success = true,
					publicIpAddress = s.publicIpAddress,
					publicHttpPort = s.publicHttpPort,
					abuseContact = s.abuseContact,
					operatorName = s.operatorName,
					adminIpAddress = s.adminIpAddress,
					adminHttpPort = s.adminHttpPort,
					adminHttpsPort = s.adminHttpsPort,
					defaultBucketName = s.defaultBucketName,
					bucketDefaults = s.bucketDefaults,
					buckets = s.buckets,
					permissiveKeys = s.permissiveKeys,
					rateLimits = s.rateLimits,
					maintenance = s.maintenance,
					dataDirectory = KVStoreService.DataDirectory
				});
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Creates a bucket (if originalName is empty) or updates/renames an existing bucket.
		/// </summary>
		public async Task<ActionResult> SaveBucket()
		{
			try
			{
				SaveBucketRequest request = await ParseRequest<SaveBucketRequest>(CancellationToken).ConfigureAwait(false);
				if (request?.bucket == null)
					return ApiError("Missing bucket.");
				if (!KvNames.TryNormalizeBucketName(request.bucket.name, out string newName))
					return ApiError("Bucket names must be 1-" + KvNames.BucketNameMaxLength + " characters from the base32 alphabet (a-z, 2-7).");
				BucketConfig incoming = request.bucket.Clone();
				incoming.name = newName;
				incoming.RepairLimits();

				Settings s = KVStoreService.CloneSettingsObjectSlow();
				KvStorage storage = KVStoreService.Engine.Storage;
				string message;
				if (string.IsNullOrEmpty(request.originalName))
				{
					if (s.buckets.Any(b => b.name == newName))
						return ApiError("A bucket named \"" + newName + "\" already exists.");
					// Delete any leftover items stored under this name (e.g. from a deletion interrupted by a crash) before the bucket becomes reachable.
					storage.FlushBucket(newName);
					s.buckets.Add(incoming);
					message = "Bucket \"" + newName + "\" was created.";
				}
				else
				{
					if (!KvNames.TryNormalizeBucketName(request.originalName, out string oldName))
						return ApiError("Invalid original bucket name.");
					int index = s.buckets.FindIndex(b => b.name == oldName);
					if (index < 0)
						return ApiError("Bucket \"" + oldName + "\" does not exist.");
					bool isDefault = oldName == s.defaultBucketName;
					if (isDefault && !incoming.enabled)
						return ApiError("The default bucket can not be disabled.");
					if (newName != oldName)
					{
						if (s.buckets.Any(b => b.name == newName))
							return ApiError("A bucket named \"" + newName + "\" already exists.");
						storage.RenameBucket(oldName, newName);
						if (isDefault)
							s.defaultBucketName = newName;
						message = "Bucket \"" + oldName + "\" was renamed to \"" + newName + "\" and saved.";
					}
					else
						message = "Bucket \"" + newName + "\" was saved.";
					s.buckets[index] = incoming;
				}
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				KVStoreService.Engine.InvalidateItemStats();
				return Json(new { success = true, message });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes a bucket and all of its items.  The default bucket can not be deleted.
		/// </summary>
		public async Task<ActionResult> DeleteBucket()
		{
			try
			{
				NameRequest request = await ParseRequest<NameRequest>(CancellationToken).ConfigureAwait(false);
				if (!KvNames.TryNormalizeBucketName(request?.name, out string name))
					return ApiError("Invalid bucket name.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				if (name == s.defaultBucketName)
					return ApiError("The default bucket can not be deleted.");
				if (s.buckets.RemoveAll(b => b.name == name) == 0)
					return ApiError("Bucket \"" + name + "\" does not exist.");
				// Remove the bucket from the configuration first so no new items can arrive, then delete its items.
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				int deleted = KVStoreService.Engine.Storage.FlushBucket(name);
				KVStoreService.Engine.InvalidateItemStats();
				return Json(new { success = true, message = "Bucket \"" + name + "\" was deleted, along with " + deleted + " item(s)." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Makes an existing bucket the default bucket (enabling it if necessary).
		/// </summary>
		public async Task<ActionResult> SetDefaultBucket()
		{
			try
			{
				NameRequest request = await ParseRequest<NameRequest>(CancellationToken).ConfigureAwait(false);
				if (!KvNames.TryNormalizeBucketName(request?.name, out string name))
					return ApiError("Invalid bucket name.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				BucketConfig bucket = s.buckets.FirstOrDefault(b => b.name == name);
				if (bucket == null)
					return ApiError("Bucket \"" + name + "\" does not exist.");
				bucket.enabled = true;
				s.defaultBucketName = name;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "\"" + name + "\" is now the default bucket." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Saves the template applied to newly created buckets.
		/// </summary>
		public async Task<ActionResult> SaveBucketDefaults()
		{
			try
			{
				BucketConfig request = await ParseRequest<BucketConfig>(CancellationToken).ConfigureAwait(false);
				if (request == null)
					return ApiError("Missing bucket defaults.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				s.bucketDefaults = request;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "Global defaults were saved.  They apply to buckets created from now on." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Saves the rate limit settings.  The rate limiters are replaced, which resets every client's buckets to full.
		/// </summary>
		public async Task<ActionResult> SaveRateLimits()
		{
			try
			{
				RateLimitSettings request = await ParseRequest<RateLimitSettings>(CancellationToken).ConfigureAwait(false);
				if (request == null)
					return ApiError("Missing rate limits.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				s.rateLimits = request;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "Rate limits were saved and are in effect.  Every client's rate limit buckets were reset to full." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Saves the key format (strict or permissive).
		/// </summary>
		public async Task<ActionResult> SaveKeyFormat()
		{
			try
			{
				KeyFormatRequest request = await ParseRequest<KeyFormatRequest>(CancellationToken).ConfigureAwait(false);
				if (request == null)
					return ApiError("Missing key format.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				s.permissiveKeys = request.permissiveKeys;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "Key format was saved: " + (s.permissiveKeys ? "permissive." : "strict.") });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Saves the maintenance settings.
		/// </summary>
		public async Task<ActionResult> SaveMaintenance()
		{
			try
			{
				MaintenanceSettings request = await ParseRequest<MaintenanceSettings>(CancellationToken).ConfigureAwait(false);
				if (request == null)
					return ApiError("Missing maintenance settings.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				s.maintenance = request;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "Maintenance settings were saved." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Saves the public listener binding and the information shown on the landing page.
		/// </summary>
		public async Task<ActionResult> SaveGeneral()
		{
			try
			{
				GeneralRequest request = await ParseRequest<GeneralRequest>(CancellationToken).ConfigureAwait(false);
				if (request == null)
					return ApiError("Missing settings.");
				Settings s = KVStoreService.CloneSettingsObjectSlow();
				s.publicIpAddress = request.publicIpAddress;
				s.publicHttpPort = request.publicHttpPort;
				s.abuseContact = request.abuseContact;
				s.operatorName = request.operatorName;
				await KVStoreService.SaveNewSettings(s, CancellationToken).ConfigureAwait(false);
				return Json(new { success = true, message = "General settings were saved." });
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
	}
	public class SaveBucketRequest
	{
		/// <summary>
		/// Name of the bucket being edited, or null/empty to create a new bucket.
		/// </summary>
		public string originalName;
		/// <summary>
		/// The bucket's new configuration.
		/// </summary>
		public BucketConfig bucket;
	}
	public class NameRequest
	{
		public string name;
	}
	public class KeyFormatRequest
	{
		public bool permissiveKeys;
	}
	public class GeneralRequest
	{
		public string publicIpAddress;
		public int publicHttpPort;
		public string abuseContact;
		public string operatorName;
	}
}
