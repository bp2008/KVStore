using BPUtil;
using LiteDB;
using LiteDB.Engine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace KVStore
{
	/// <summary>
	/// <para>The storage repository.  Values are files on disk; LiteDB stores only metadata (<see cref="KvMeta"/>).  This is the only class that references the <see cref="LiteDatabase"/>.</para>
	/// <para>Layout: <c>&lt;data&gt;/blobs/&lt;bucket&gt;/&lt;k0&gt;&lt;k1&gt;/&lt;key&gt;.bin</c> for values, <c>&lt;data&gt;/tmp/</c> for uploads in progress, <c>&lt;data&gt;/kvstore.db</c> for metadata.</para>
	/// <para>Database damage is treated as a normal event: the damaged database is set aside, a fresh database is created, all values are deleted (their metadata is gone), the event is counted, and the service keeps running.</para>
	/// <para>Locking: every database operation holds <see cref="rw"/> in read mode, except operations that replace or restructure the whole database, which hold it in write mode.  Operations that change a bucket's items additionally hold that bucket's lock, which serializes mutations of the bucket so that the value files, metadata, and usage counters stay consistent.  Lock order is always <see cref="rw"/> first, then a bucket lock.</para>
	/// </summary>
	public class KvStorage : IDisposable
	{
		/// <summary>
		/// File name of the metadata database.
		/// </summary>
		public const string DatabaseFileName = "kvstore.db";
		/// <summary>
		/// Number of damaged database files (".corrupt.&lt;timestamp&gt;") to keep.
		/// </summary>
		public const int CorruptFilesToKeep = 3;
		/// <summary>
		/// Root data directory.
		/// </summary>
		public readonly string DataDirectory;
		/// <summary>
		/// Directory containing value files.
		/// </summary>
		public readonly string BlobDirectory;
		/// <summary>
		/// Directory where uploads are written before they are moved into <see cref="BlobDirectory"/>.
		/// </summary>
		public readonly string TempDirectory;
		/// <summary>
		/// Path of the LiteDB database file.
		/// </summary>
		public readonly string DatabasePath;
		/// <summary>
		/// Path of the LiteDB log file, which LiteDB keeps next to the database file.
		/// </summary>
		public readonly string DatabaseLogPath;
		private readonly string recoveryStatePath;
		private readonly RecoveryState recoveryState = new RecoveryState();
		private readonly OpStats stats;
		private readonly Func<long> getUnixTimeSeconds;
		private readonly ReaderWriterLockSlim rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		private readonly ConcurrentDictionary<string, object> bucketLocks = new ConcurrentDictionary<string, object>();
		private readonly ConcurrentDictionary<string, BucketUsage> usage = new ConcurrentDictionary<string, BucketUsage>();
		private LiteDatabase db;
		private ILiteCollection<KvMeta> col;
		/// <summary>
		/// Incremented each time the database is recovered, so that concurrent operations which all observed the same damage cause only one recovery.
		/// </summary>
		private long generation = 0;
		private volatile bool disposed = false;
		private string cachedDriveRoot = null;

		/// <summary>
		/// Results of the most recent orphan sweep.
		/// </summary>
		public OrphanSweepResult LastOrphanSweep { get; private set; }
		/// <summary>
		/// Number of times the database has been recovered (persistent).
		/// </summary>
		public long RecoveryEventCount => Interlocked.Read(ref recoveryState.recoveryEventCount);
		/// <summary>
		/// Time of the last database recovery in seconds since the unix epoch, or 0 if there has never been one (persistent).
		/// </summary>
		public long LastRecoveryUnixSeconds => Interlocked.Read(ref recoveryState.lastRecoveryUnixSeconds);
		/// <summary>
		/// Reason for the last database recovery, or null (persistent).
		/// </summary>
		public string LastRecoveryReason => recoveryState.lastRecoveryReason;

		/// <summary>
		/// Constructs a KvStorage.  Call <see cref="Open"/> before use.
		/// </summary>
		/// <param name="dataDirectory">Root data directory.  It should be outside the published binary directory.</param>
		/// <param name="stats">Counters to update.</param>
		/// <param name="getUnixTimeSeconds">A function that returns the current time in seconds since the unix epoch.</param>
		public KvStorage(string dataDirectory, OpStats stats, Func<long> getUnixTimeSeconds)
		{
			DataDirectory = Path.GetFullPath(dataDirectory);
			BlobDirectory = Path.Combine(DataDirectory, "blobs");
			TempDirectory = Path.Combine(DataDirectory, "tmp");
			DatabasePath = Path.Combine(DataDirectory, DatabaseFileName);
			DatabaseLogPath = Path.Combine(DataDirectory, Path.GetFileNameWithoutExtension(DatabaseFileName) + "-log" + Path.GetExtension(DatabaseFileName));
			recoveryStatePath = Path.Combine(DataDirectory, "RecoveryState.json");
			this.stats = stats;
			this.getUnixTimeSeconds = getUnixTimeSeconds;
		}

		#region Open / Recovery
		/// <summary>
		/// Opens the database, verifying it by reading every metadata record.  If the database is damaged, it is recovered.  Temporary files left by a previous process are deleted.
		/// </summary>
		public void Open()
		{
			Directory.CreateDirectory(DataDirectory);
			Directory.CreateDirectory(BlobDirectory);
			Directory.CreateDirectory(TempDirectory);
			if (File.Exists(recoveryStatePath))
			{
				try
				{
					recoveryState.Load(recoveryStatePath);
				}
				catch (Exception ex)
				{
					KVStoreService.ReportError(ex, "Unable to load " + recoveryStatePath);
				}
			}
			DeleteDirectoryContents(TempDirectory);

			rw.EnterWriteLock();
			try
			{
				try
				{
					OpenDatabase();
					LoadUsage();
				}
				catch (Exception ex)
				{
					RecoverInternal("The database could not be opened or read at startup.", ex);
				}
			}
			finally
			{
				rw.ExitWriteLock();
			}
		}
		/// <summary>
		/// Opens (or creates) the database file.  Hold the write lock when calling this.
		/// </summary>
		private void OpenDatabase()
		{
			// LiteDB's default collation ignores case, but permissive-mode keys are case-sensitive, so IDs must be compared ordinally.
			// A database file's collation is fixed when the file is created, and LiteDB refuses to open an existing file whose collation differs from the one requested, so the collation is requested only when creating a new file.  An existing file with another collation is converted.
			ConnectionString cs = new ConnectionString { Filename = DatabasePath, Connection = ConnectionType.Direct };
			if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0)
				cs.Collation = Collation.Binary;
			db = new LiteDatabase(cs);
			if (db.Collation.SortOptions != CompareOptions.Ordinal)
				db.Rebuild(new RebuildOptions { Collation = Collation.Binary });
			col = db.GetCollection<KvMeta>("meta");
			col.EnsureIndex(x => x.Expires);
			col.EnsureIndex(x => x.Bucket);
			col.EnsureIndex(x => x.Created);
		}
		/// <summary>
		/// Computes the per-bucket usage counters by reading every metadata record.  Hold the write lock when calling this.
		/// </summary>
		private void LoadUsage()
		{
			usage.Clear();
			foreach (KvMeta m in col.FindAll())
			{
				BucketUsage u = usage.GetOrAdd(m.Bucket, b => new BucketUsage());
				u.Add(1, m.Size);
			}
		}
		/// <summary>
		/// Recovers from database damage: sets the damaged database aside, creates a fresh database, deletes all value files, and records the event.  Hold the write lock when calling this.
		/// </summary>
		/// <param name="reason">Short description of why recovery is happening.</param>
		/// <param name="ex">The exception which revealed the damage.</param>
		private void RecoverInternal(string reason, Exception ex)
		{
			KVStoreService.ReportError(ex, "DATABASE RECOVERY: " + reason + " The database will be replaced with a fresh one and all stored items will be deleted.");
			try
			{
				db?.Dispose();
			}
			catch (Exception disposeEx)
			{
				KVStoreService.ReportError(disposeEx, "DATABASE RECOVERY: Error disposing damaged database.");
			}
			db = null;
			col = null;

			string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
			SetAside(DatabasePath, DatabasePath + ".corrupt." + timestamp);
			SetAside(DatabaseLogPath, DatabaseLogPath + ".corrupt." + timestamp);
			PruneCorruptFiles(DatabasePath);
			PruneCorruptFiles(DatabaseLogPath);

			// All metadata is gone, so every value file is unreachable.
			DeleteDirectoryContents(BlobDirectory);
			usage.Clear();
			generation++;

			lock (recoveryState)
			{
				Interlocked.Increment(ref recoveryState.recoveryEventCount);
				Interlocked.Exchange(ref recoveryState.lastRecoveryUnixSeconds, getUnixTimeSeconds());
				recoveryState.lastRecoveryReason = reason + " " + (ex == null ? "" : IpScrubber.Scrub(ex.GetType().Name + ": " + ex.Message));
				try
				{
					recoveryState.Save(recoveryStatePath);
				}
				catch (Exception saveEx)
				{
					KVStoreService.ReportError(saveEx, "DATABASE RECOVERY: Unable to save " + recoveryStatePath);
				}
			}

			try
			{
				OpenDatabase();
				Logger.Info("DATABASE RECOVERY: A fresh database was created.  Recovery event count: " + RecoveryEventCount);
			}
			catch (Exception openEx)
			{
				db = null;
				col = null;
				KVStoreService.ReportError(openEx, "DATABASE RECOVERY: Unable to create a fresh database.  Storage is unavailable until the next recovery attempt.");
			}
		}
		private static void SetAside(string path, string newPath)
		{
			try
			{
				if (File.Exists(path))
					File.Move(path, newPath);
			}
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "DATABASE RECOVERY: Unable to rename \"" + path + "\".  Deleting it instead.");
				try
				{
					File.Delete(path);
				}
				catch (Exception deleteEx)
				{
					KVStoreService.ReportError(deleteEx, "DATABASE RECOVERY: Unable to delete \"" + path + "\".");
				}
			}
		}
		/// <summary>
		/// Deletes all but the newest <see cref="CorruptFilesToKeep"/> damaged copies of the given file.
		/// </summary>
		private void PruneCorruptFiles(string originalPath)
		{
			try
			{
				string prefix = Path.GetFileName(originalPath) + ".corrupt.";
				FileInfo[] corruptFiles = new DirectoryInfo(DataDirectory).GetFiles(prefix + "*")
					.OrderByDescending(f => f.Name, StringComparer.Ordinal) // Timestamps sort chronologically.
					.ToArray();
				foreach (FileInfo fi in corruptFiles.Skip(CorruptFilesToKeep))
					fi.Delete();
			}
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "DATABASE RECOVERY: Unable to prune old damaged database files.");
			}
		}
		/// <summary>
		/// Returns true if the exception indicates that the database is damaged or its engine has shut down, such that it must be recovered.
		/// </summary>
		/// <param name="ex">Exception thrown during a database operation.</param>
		/// <returns></returns>
		public static bool IsStructuralDamage(Exception ex)
		{
			if (ex is LiteException le)
			{
				switch (le.ErrorCode)
				{
					case LiteException.DATABASE_SHUTDOWN:
					case LiteException.INVALID_DATABASE:
					case LiteException.ENGINE_DISPOSED:
					case LiteException.INVALID_FREE_SPACE_PAGE:
					case LiteException.INVALID_DATAFILE_STATE:
						return true;
					default:
						return false;
				}
			}
			// Damaged pages often surface as runtime errors inside LiteDB's page and document readers.  LiteDB closes its engine after such an error, so even if one is missed here, the next operation throws ENGINE_DISPOSED or DATABASE_SHUTDOWN.
			if (ex is InvalidCastException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException || ex is NullReferenceException || ex is OverflowException || ex is System.IO.EndOfStreamException)
			{
				string ns = ex.TargetSite?.DeclaringType?.Namespace;
				return ns != null && (ns == "LiteDB" || ns.StartsWith("LiteDB.", StringComparison.Ordinal));
			}
			return false;
		}
		/// <summary>
		/// Recovers the database after an operation observed damage, unless another operation already recovered it.
		/// </summary>
		/// <param name="damage">The exception which revealed the damage.</param>
		/// <param name="observedGeneration">The value of <see cref="generation"/> when the failed operation began.</param>
		private void RecoverAfterDamage(Exception damage, long observedGeneration)
		{
			rw.EnterWriteLock();
			try
			{
				if (generation == observedGeneration && !disposed)
					RecoverInternal("Damage was detected while the service was running.", damage);
			}
			finally
			{
				rw.ExitWriteLock();
			}
		}
		/// <summary>
		/// If the database could not be created during a previous recovery, tries again.
		/// </summary>
		public void RetryRecoveryIfUnavailable()
		{
			if (col != null || disposed)
				return;
			rw.EnterWriteLock();
			try
			{
				if (col == null && !disposed)
				{
					try
					{
						OpenDatabase();
						LoadUsage();
					}
					catch (Exception ex)
					{
						RecoverInternal("The database was unavailable.", ex);
					}
				}
			}
			finally
			{
				rw.ExitWriteLock();
			}
		}
		/// <summary>
		/// Runs a database operation while holding the read lock.  If the operation reveals database damage, the database is recovered and <see cref="StorageRecoveredException"/> is thrown.
		/// </summary>
		private T WithReadLock<T>(Func<T> operation)
		{
			ThrowIfUnavailable();
			rw.EnterReadLock();
			long observedGeneration = generation;
			Exception damage;
			try
			{
				ThrowIfUnavailable();
				try
				{
					return operation();
				}
				catch (Exception ex) when (IsStructuralDamage(ex) && !disposed)
				{
					damage = ex;
				}
			}
			finally
			{
				rw.ExitReadLock();
			}
			RecoverAfterDamage(damage, observedGeneration);
			throw new StorageRecoveredException(damage);
		}
		/// <summary>
		/// Runs a database operation while holding the write lock.  If the operation reveals database damage, the database is recovered and <see cref="StorageRecoveredException"/> is thrown.
		/// </summary>
		private T WithWriteLock<T>(Func<T> operation)
		{
			ThrowIfUnavailable();
			rw.EnterWriteLock();
			try
			{
				ThrowIfUnavailable();
				try
				{
					return operation();
				}
				catch (Exception ex) when (IsStructuralDamage(ex) && !disposed)
				{
					RecoverInternal("Damage was detected while the service was running.", ex);
					throw new StorageRecoveredException(ex);
				}
			}
			finally
			{
				rw.ExitWriteLock();
			}
		}
		private void ThrowIfUnavailable()
		{
			if (disposed)
				throw new ObjectDisposedException(nameof(KvStorage));
			if (col == null)
				throw new StorageUnavailableException();
		}
		#endregion

		#region Paths and Helpers
		/// <summary>
		/// Returns the path of the value file for the given bucket and key.
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <param name="key">Normalized key.</param>
		/// <returns></returns>
		public string GetBlobPath(string bucket, string key)
		{
			string stem = KvNames.GetFileStem(key);
			return Path.Combine(BlobDirectory, bucket, KvNames.GetShardDirectoryName(stem), stem + ".bin");
		}
		/// <summary>
		/// Returns a path in <see cref="TempDirectory"/> where a new upload can be written.
		/// </summary>
		/// <returns></returns>
		public string CreateTempFilePath()
		{
			return Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
		}
		private object GetBucketLock(string bucket)
		{
			return bucketLocks.GetOrAdd(bucket, b => new object());
		}
		private long Now()
		{
			return getUnixTimeSeconds();
		}
		/// <summary>
		/// Returns the metadata with exactly the given ID (ordinal comparison), or null.  Hold the read lock when calling this.
		/// </summary>
		private KvMeta FindExact(string id)
		{
			KvMeta m = col.FindById(id);
			return m != null && string.Equals(m.Id, id, StringComparison.Ordinal) ? m : null;
		}
		/// <summary>
		/// Deletes the value file and metadata of an item.  Hold the read lock and the item's bucket lock when calling this.
		/// </summary>
		private void DeleteItemInternal(KvMeta m)
		{
			col.Delete(m.Id);
			if (usage.TryGetValue(m.Bucket, out BucketUsage u))
				u.Add(-1, -m.Size);
			TryDeleteFile(GetBlobPath(m.Bucket, m.GetKey()));
		}
		private static void TryDeleteFile(string path)
		{
			try
			{
				File.Delete(path);
			}
			catch (DirectoryNotFoundException) { }
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "Unable to delete \"" + path + "\".");
			}
		}
		private static void DeleteDirectoryContents(string directory)
		{
			try
			{
				DirectoryInfo di = new DirectoryInfo(directory);
				if (!di.Exists)
					return;
				foreach (DirectoryInfo sub in di.GetDirectories())
					sub.Delete(true);
				foreach (FileInfo fi in di.GetFiles())
					fi.Delete();
			}
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "Unable to delete the contents of \"" + directory + "\".");
			}
		}
		private static void DeleteDirectory(string directory)
		{
			try
			{
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "Unable to delete \"" + directory + "\".");
			}
		}
		/// <summary>
		/// Moves the file, replacing any existing file.  Retries briefly because on Windows the target may be open for reading.
		/// </summary>
		private static void MoveReplacing(string source, string destination)
		{
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					File.Move(source, destination, true);
					return;
				}
				catch (IOException) when (attempt < 5 && File.Exists(source))
				{
					Thread.Sleep(20 * attempt);
				}
				catch (UnauthorizedAccessException) when (attempt < 5 && File.Exists(source))
				{
					Thread.Sleep(20 * attempt);
				}
			}
		}
		/// <summary>
		/// Returns the number of bytes available on the volume containing the data directory, or -1 if unknown.
		/// </summary>
		/// <returns></returns>
		public long GetFreeDiskBytes()
		{
			try
			{
				string root = cachedDriveRoot;
				if (root == null)
				{
					// DriveInfo requires a volume root (Windows) or mount point (Linux), so find the one with the longest path that contains the data directory.
					string dataPath = DataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
					StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
					root = DriveInfo.GetDrives()
						.Select(d => d.RootDirectory.FullName)
						.Where(r => dataPath.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmp))
						.OrderByDescending(r => r.Length)
						.FirstOrDefault();
					if (root == null)
						return -1;
					cachedDriveRoot = root;
				}
				return new DriveInfo(root).AvailableFreeSpace;
			}
			catch (Exception)
			{
				return -1;
			}
		}
		#endregion

		#region Item Operations
		/// <summary>
		/// Returns the metadata of the item if it exists and has not expired, otherwise null.  Reads never extend an item's lifetime.
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <param name="key">Normalized key.</param>
		/// <returns></returns>
		public KvMeta GetLive(string bucket, string key)
		{
			string id = KvNames.GetId(bucket, key);
			long now = Now();
			return WithReadLock(() =>
			{
				KvMeta m = FindExact(id);
				return m != null && m.Expires > now ? m : null;
			});
		}
		/// <summary>
		/// Returns the metadata of the item and an open stream of its value, if the item exists and has not expired, otherwise null.  The caller must dispose the stream.  The stream remains readable even if the item is overwritten or deleted while it is being read.
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <param name="key">Normalized key.</param>
		/// <returns></returns>
		public ItemReader OpenLive(string bucket, string key)
		{
			string id = KvNames.GetId(bucket, key);
			long now = Now();
			return WithReadLock(() =>
			{
				lock (GetBucketLock(bucket))
				{
					KvMeta m = FindExact(id);
					if (m == null || m.Expires <= now)
						return null;
					try
					{
						FileStream fs = new FileStream(GetBlobPath(bucket, key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
						return new ItemReader(m, fs);
					}
					catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
					{
						// Metadata without a value file.  This should not happen because values are written before metadata and deleted after it, but it is harmless to clean up now.
						col.Delete(m.Id);
						if (usage.TryGetValue(m.Bucket, out BucketUsage u))
							u.Add(-1, -m.Size);
						stats.Add(OpCounter.OrphanMetadataRemoved);
						return null;
					}
				}
			});
		}
		/// <summary>
		/// <para>Stores an item whose value has already been written to a temporary file.  The file is moved into place and then the metadata is written, so a crash can leave an orphaned file (reclaimed by the orphan sweep) but never metadata without a file.</para>
		/// <para>If the bucket is at its item count or byte quota, the items closest to expiration (expired ones first) are evicted to make room.</para>
		/// </summary>
		/// <param name="bucket">The bucket configuration.</param>
		/// <param name="key">Normalized key.</param>
		/// <param name="tempFilePath">Path of the temporary file containing the value.  On success, the file is moved.  On failure, the caller should delete it.</param>
		/// <param name="size">Size of the value in bytes.</param>
		/// <param name="ttlSeconds">Time to live in seconds.</param>
		/// <param name="minFreeDiskBytes">Refuse the upload if storing it would leave less than this many bytes free on the data volume.</param>
		/// <returns></returns>
		public PutResult CommitPut(BucketConfig bucket, string key, string tempFilePath, long size, int ttlSeconds, long minFreeDiskBytes)
		{
			if (size > bucket.maxTotalBytes)
				return PutResult.StorageFull();
			long free = GetFreeDiskBytes();
			if (free >= 0 && free < minFreeDiskBytes + size)
				return PutResult.StorageFull();

			string bucketName = bucket.name;
			string id = KvNames.GetId(bucketName, key);
			return WithReadLock(() =>
			{
				lock (GetBucketLock(bucketName))
				{
					long now = Now();
					KvMeta existing = FindExact(id);
					BucketUsage u = usage.GetOrAdd(bucketName, b => new BucketUsage());
					long excessCount = u.Count + (existing == null ? 1 : 0) - bucket.maxItemCount;
					long excessBytes = u.Bytes - (existing?.Size ?? 0) + size - bucket.maxTotalBytes;
					if ((excessCount > 0 || excessBytes > 0) && !EvictInternal(bucketName, id, excessCount, excessBytes, now))
						return PutResult.StorageFull();

					string path = GetBlobPath(bucketName, key);
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					MoveReplacing(tempFilePath, path);

					KvMeta m = new KvMeta()
					{
						Id = id,
						Bucket = bucketName,
						Size = size,
						Created = now,
						Expires = now + ttlSeconds
					};
					col.Upsert(m);
					if (existing != null)
						u.Add(0, size - existing.Size);
					else
						u.Add(1, size);
					return PutResult.Success(m.Expires);
				}
			});
		}
		/// <summary>
		/// Evicts items from the bucket until at least the given number of items and bytes have been freed, in order of expiration time (so expired items go first).  Returns false if not enough could be freed.  Hold the read lock and the bucket lock when calling this.
		/// </summary>
		private bool EvictInternal(string bucketName, string excludeId, long countToFree, long bytesToFree, long now)
		{
			const int maxEvictionsPerCall = 1000;
			long freedCount = 0;
			long freedBytes = 0;
			// Materialize the victims before deleting, because the collection must not be modified while a query is being enumerated.
			List<KvMeta> victims = new List<KvMeta>();
			foreach (KvMeta m in col.Find(Query.All(nameof(KvMeta.Expires), Query.Ascending)))
			{
				if (freedCount >= countToFree && freedBytes >= bytesToFree)
					break;
				if (victims.Count >= maxEvictionsPerCall)
					break;
				if (m.Bucket != bucketName || m.Id == excludeId)
					continue;
				victims.Add(m);
				freedCount++;
				freedBytes += m.Size;
			}
			foreach (KvMeta m in victims)
			{
				DeleteItemInternal(m);
				stats.Add(m.Expires <= now ? OpCounter.ExpiredSwept : OpCounter.QuotaEvicted);
			}
			return freedCount >= countToFree && freedBytes >= bytesToFree;
		}
		/// <summary>
		/// Deletes an item.  Returns true if a live (unexpired) item was deleted.
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <param name="key">Normalized key.</param>
		/// <returns></returns>
		public bool Delete(string bucket, string key)
		{
			string id = KvNames.GetId(bucket, key);
			return WithReadLock(() =>
			{
				lock (GetBucketLock(bucket))
				{
					KvMeta existing = FindExact(id);
					if (existing != null)
						DeleteItemInternal(existing);
					else
						TryDeleteFile(GetBlobPath(bucket, key)); // Make sure a takedown leaves nothing behind, even an orphan.
					return existing != null && existing.Expires > Now();
				}
			});
		}
		#endregion

		#region Maintenance
		/// <summary>
		/// Deletes expired items.  Returns the number of items deleted.
		/// </summary>
		/// <param name="maxItems">Maximum number of items to delete in this call.</param>
		/// <returns></returns>
		public int SweepExpired(int maxItems = 20000)
		{
			int deleted = 0;
			while (deleted < maxItems)
			{
				long now = Now();
				List<KvMeta> batch = WithReadLock(() => col.Find(Query.LTE(nameof(KvMeta.Expires), now), 0, 500).ToList());
				if (batch.Count == 0)
					break;
				foreach (KvMeta candidate in batch)
				{
					bool didDelete = WithReadLock(() =>
					{
						lock (GetBucketLock(candidate.Bucket))
						{
							KvMeta current = FindExact(candidate.Id);
							if (current == null || current.Expires > now)
								return false; // Deleted or overwritten since the query.
							DeleteItemInternal(current);
							return true;
						}
					});
					if (didDelete)
					{
						deleted++;
						stats.Add(OpCounter.ExpiredSwept);
					}
				}
				if (batch.Count < 500)
					break;
			}
			return deleted;
		}
		/// <summary>
		/// <para>Deletes value files that have no metadata, abandoned temporary files, and metadata whose value file is missing.  Files younger than <paramref name="minAgeSeconds"/> are never deleted, to avoid racing an upload in progress.</para>
		/// <para>Also measures the size of the blob tree.</para>
		/// </summary>
		/// <param name="minAgeSeconds">Minimum age (by last write time) of a file for it to be deleted.</param>
		/// <returns></returns>
		public OrphanSweepResult SweepOrphans(int minAgeSeconds)
		{
			OrphanSweepResult result = new OrphanSweepResult();
			DateTime cutoff = DateTime.UtcNow.AddSeconds(-minAgeSeconds);

			// Abandoned uploads.
			foreach (FileInfo fi in SafeGetFiles(new DirectoryInfo(TempDirectory)))
			{
				if (fi.LastWriteTimeUtc < cutoff && TryDeleteOrphan(fi))
					result.filesReclaimed++;
			}

			// Value files without metadata.
			foreach (DirectoryInfo bucketDir in SafeGetDirectories(new DirectoryInfo(BlobDirectory)))
			{
				if (!KvNames.TryNormalizeBucketName(bucketDir.Name, out string bucketName) || bucketName != bucketDir.Name)
					continue; // Not a directory that KVStore created.
				foreach (DirectoryInfo shardDir in SafeGetDirectories(bucketDir))
				{
					foreach (FileInfo fi in SafeGetFiles(shardDir))
					{
						if (fi.Extension == ".bin" && KvNames.TryGetKeyFromFileStem(Path.GetFileNameWithoutExtension(fi.Name), out string key)
							&& GetBlobPath(bucketName, key) == fi.FullName)
						{
							string id = KvNames.GetId(bucketName, key);
							bool reclaimed = fi.LastWriteTimeUtc < cutoff && WithReadLock(() =>
							{
								lock (GetBucketLock(bucketName))
								{
									if (FindExact(id) != null)
										return false;
									return TryDeleteOrphan(fi);
								}
							});
							if (reclaimed)
								result.filesReclaimed++;
							else
							{
								result.blobTreeFiles++;
								result.blobTreeBytes += SafeGetLength(fi);
							}
						}
						else if (fi.LastWriteTimeUtc < cutoff && TryDeleteOrphan(fi))
							result.filesReclaimed++; // Stray temporary or unrecognized file.
						else
						{
							result.blobTreeFiles++;
							result.blobTreeBytes += SafeGetLength(fi);
						}
					}
				}
			}

			// Metadata without value files.
			List<KvMeta> all = WithReadLock(() => col.FindAll().ToList());
			foreach (KvMeta m in all)
			{
				if (File.Exists(GetBlobPath(m.Bucket, m.GetKey())))
					continue;
				bool removed = WithReadLock(() =>
				{
					lock (GetBucketLock(m.Bucket))
					{
						KvMeta current = FindExact(m.Id);
						if (current == null || File.Exists(GetBlobPath(current.Bucket, current.GetKey())))
							return false;
						DeleteItemInternal(current);
						return true;
					}
				});
				if (removed)
					result.metadataRemoved++;
			}

			stats.Add(OpCounter.OrphanFilesReclaimed, result.filesReclaimed);
			stats.Add(OpCounter.OrphanMetadataRemoved, result.metadataRemoved);
			result.completedUnixSeconds = Now();
			LastOrphanSweep = result;
			return result;
		}
		private static bool TryDeleteOrphan(FileInfo fi)
		{
			try
			{
				fi.Delete();
				return true;
			}
			catch (Exception ex)
			{
				KVStoreService.ReportError(ex, "Orphan sweep was unable to delete \"" + fi.FullName + "\".");
				return false;
			}
		}
		private static IEnumerable<FileInfo> SafeGetFiles(DirectoryInfo di)
		{
			try
			{
				return di.Exists ? di.GetFiles() : new FileInfo[0];
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return new FileInfo[0];
			}
		}
		private static IEnumerable<DirectoryInfo> SafeGetDirectories(DirectoryInfo di)
		{
			try
			{
				return di.Exists ? di.GetDirectories() : new DirectoryInfo[0];
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return new DirectoryInfo[0];
			}
		}
		private static long SafeGetLength(FileInfo fi)
		{
			try
			{
				return fi.Length;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return 0;
			}
		}
		/// <summary>
		/// Flushes the database log into the database file.
		/// </summary>
		public void Checkpoint()
		{
			WithReadLock(() =>
			{
				db.Checkpoint();
				return true;
			});
		}
		/// <summary>
		/// Rebuilds the database file, reclaiming unused space.  All other storage operations wait until this is finished.  Returns the number of bytes by which the database file shrank.
		/// </summary>
		/// <returns></returns>
		public long Rebuild()
		{
			// Rebuild without options would reset the file to LiteDB's default (case-insensitive) collation.
			return WithWriteLock(() => db.Rebuild(new RebuildOptions { Collation = Collation.Binary }));
		}
		#endregion

		#region Bucket Operations
		/// <summary>
		/// Deletes every item in a bucket.  Returns the number of items deleted.
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <returns></returns>
		public int FlushBucket(string bucket)
		{
			return WithReadLock(() =>
			{
				lock (GetBucketLock(bucket))
				{
					int deleted = col.DeleteMany(x => x.Bucket == bucket);
					usage.TryRemove(bucket, out BucketUsage ignored);
					DeleteDirectory(Path.Combine(BlobDirectory, bucket));
					return deleted;
				}
			});
		}
		/// <summary>
		/// Deletes every item in every bucket.  Returns the number of items deleted.
		/// </summary>
		/// <returns></returns>
		public int FlushAll()
		{
			return WithWriteLock(() =>
			{
				int deleted = col.DeleteAll();
				usage.Clear();
				DeleteDirectoryContents(BlobDirectory);
				return deleted;
			});
		}
		/// <summary>
		/// Moves every item from one bucket name to another, for use when a bucket is renamed.  Any items already stored under the new name are deleted first.  All other storage operations wait until this is finished.
		/// </summary>
		/// <param name="oldName">Normalized old bucket name.</param>
		/// <param name="newName">Normalized new bucket name.</param>
		public void RenameBucket(string oldName, string newName)
		{
			WithWriteLock(() =>
			{
				col.DeleteMany(x => x.Bucket == newName);
				usage.TryRemove(newName, out BucketUsage ignored);
				string newDir = Path.Combine(BlobDirectory, newName);
				DeleteDirectory(newDir);

				string oldDir = Path.Combine(BlobDirectory, oldName);
				if (Directory.Exists(oldDir))
					Directory.Move(oldDir, newDir);

				List<KvMeta> items = col.Find(x => x.Bucket == oldName).ToList();
				db.BeginTrans();
				try
				{
					foreach (KvMeta m in items)
					{
						string key = m.GetKey();
						col.Delete(m.Id);
						m.Id = KvNames.GetId(newName, key);
						m.Bucket = newName;
						col.Insert(m);
					}
					db.Commit();
				}
				catch
				{
					db.Rollback();
					throw;
				}
				if (usage.TryRemove(oldName, out BucketUsage u))
					usage[newName] = u;
				return true;
			});
		}
		/// <summary>
		/// Returns the number of items and bytes currently stored in the bucket (including expired items that have not been swept yet).
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <returns></returns>
		public BucketUsageSnapshot GetUsage(string bucket)
		{
			if (usage.TryGetValue(bucket, out BucketUsage u))
				return u.GetSnapshot();
			return new BucketUsageSnapshot(0, 0);
		}
		/// <summary>
		/// Returns the names of all buckets that currently have items in storage (including buckets that are no longer configured).
		/// </summary>
		/// <returns></returns>
		public string[] GetBucketsWithItems()
		{
			return usage.Where(kvp => kvp.Value.GetSnapshot().Count > 0).Select(kvp => kvp.Key).ToArray();
		}
		/// <summary>
		/// Reads every metadata record and computes size and expiration statistics per bucket.  Keys are never included.
		/// </summary>
		/// <returns></returns>
		public Dictionary<string, BucketItemStats> ComputeItemStats()
		{
			long now = Now();
			return WithReadLock(() =>
			{
				Dictionary<string, BucketItemStats> result = new Dictionary<string, BucketItemStats>();
				foreach (KvMeta m in col.FindAll())
				{
					if (!result.TryGetValue(m.Bucket, out BucketItemStats s))
						s = result[m.Bucket] = new BucketItemStats();
					s.Add(m, now);
				}
				return result;
			});
		}
		/// <summary>
		/// Returns the size of the database file plus its log file, in bytes.
		/// </summary>
		/// <returns></returns>
		public long GetDatabaseFileBytes()
		{
			long total = 0;
			foreach (string path in new string[] { DatabasePath, DatabaseLogPath })
			{
				try
				{
					FileInfo fi = new FileInfo(path);
					if (fi.Exists)
						total += fi.Length;
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
			}
			return total;
		}
		#endregion

		/// <summary>
		/// Checkpoints and closes the database.
		/// </summary>
		public void Dispose()
		{
			rw.EnterWriteLock();
			try
			{
				if (disposed)
					return;
				disposed = true;
				try
				{
					db?.Checkpoint();
				}
				catch (Exception ex)
				{
					KVStoreService.ReportError(ex, "Error checkpointing database during shutdown.");
				}
				try
				{
					db?.Dispose();
				}
				catch (Exception ex)
				{
					KVStoreService.ReportError(ex, "Error closing database during shutdown.");
				}
				db = null;
				col = null;
			}
			finally
			{
				rw.ExitWriteLock();
			}
		}
	}
	/// <summary>
	/// Thrown when a storage operation revealed database damage.  The database has been recovered (replaced with a fresh one), and the operation was not completed.
	/// </summary>
	public class StorageRecoveredException : Exception
	{
		/// <summary>
		/// Constructs a StorageRecoveredException.
		/// </summary>
		/// <param name="inner">The exception which revealed the damage.</param>
		public StorageRecoveredException(Exception inner) : base("Database damage was detected and the database was recovered.  The operation was not completed.", inner) { }
	}
	/// <summary>
	/// Thrown when the database is unavailable because a fresh database could not be created during recovery.
	/// </summary>
	public class StorageUnavailableException : Exception
	{
		/// <summary>
		/// Constructs a StorageUnavailableException.
		/// </summary>
		public StorageUnavailableException() : base("The database is unavailable.") { }
	}
	/// <summary>
	/// Result of <see cref="KvStorage.CommitPut"/>.
	/// </summary>
	public class PutResult
	{
		/// <summary>
		/// True if the item was stored.
		/// </summary>
		public bool Ok { get; private set; }
		/// <summary>
		/// Expiration time in seconds since the unix epoch, if <see cref="Ok"/>.
		/// </summary>
		public long Expires { get; private set; }
		/// <summary>
		/// Returns a result indicating success.
		/// </summary>
		public static PutResult Success(long expires) { return new PutResult() { Ok = true, Expires = expires }; }
		/// <summary>
		/// Returns a result indicating that the item could not be stored because a quota was reached and eviction could not free enough space.
		/// </summary>
		public static PutResult StorageFull() { return new PutResult() { Ok = false }; }
	}
	/// <summary>
	/// An open item: its metadata and a stream of its value.
	/// </summary>
	public sealed class ItemReader : IDisposable
	{
		/// <summary>
		/// The item's metadata.
		/// </summary>
		public readonly KvMeta Meta;
		/// <summary>
		/// Stream of the item's value.
		/// </summary>
		public readonly FileStream Stream;
		/// <summary>
		/// Constructs an ItemReader.
		/// </summary>
		public ItemReader(KvMeta meta, FileStream stream)
		{
			Meta = meta;
			Stream = stream;
		}
		/// <inheritdoc/>
		public void Dispose()
		{
			Stream.Dispose();
		}
	}
	/// <summary>
	/// Results of an orphan sweep.
	/// </summary>
	public class OrphanSweepResult
	{
		/// <summary>Files deleted because they had no metadata or were abandoned temporary files.</summary>
		public long filesReclaimed;
		/// <summary>Metadata records deleted because their value file was missing.</summary>
		public long metadataRemoved;
		/// <summary>Number of value files remaining in the blob tree.</summary>
		public long blobTreeFiles;
		/// <summary>Total size of the value files remaining in the blob tree, in bytes.</summary>
		public long blobTreeBytes;
		/// <summary>Time the sweep completed, in seconds since the unix epoch.</summary>
		public long completedUnixSeconds;
	}
	/// <summary>
	/// Live usage counters of one bucket.  Mutated only while holding the bucket's lock.
	/// </summary>
	public class BucketUsage
	{
		private long count;
		private long bytes;
		/// <summary>Number of items.</summary>
		public long Count => Interlocked.Read(ref count);
		/// <summary>Total size of values in bytes.</summary>
		public long Bytes => Interlocked.Read(ref bytes);
		/// <summary>
		/// Adds to the counters.
		/// </summary>
		public void Add(long countDelta, long bytesDelta)
		{
			Interlocked.Add(ref count, countDelta);
			Interlocked.Add(ref bytes, bytesDelta);
		}
		/// <summary>
		/// Returns a snapshot of the counters.
		/// </summary>
		public BucketUsageSnapshot GetSnapshot()
		{
			return new BucketUsageSnapshot(Count, Bytes);
		}
	}
	/// <summary>
	/// A snapshot of <see cref="BucketUsage"/>.
	/// </summary>
	public class BucketUsageSnapshot
	{
		/// <summary>Number of items.</summary>
		public readonly long Count;
		/// <summary>Total size of values in bytes.</summary>
		public readonly long Bytes;
		/// <summary>
		/// Constructs a BucketUsageSnapshot.
		/// </summary>
		public BucketUsageSnapshot(long count, long bytes)
		{
			Count = count;
			Bytes = bytes;
		}
	}
}
