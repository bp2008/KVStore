using BPUtil;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore.Tests
{
	/// <summary>
	/// Tests of <see cref="KvStorage"/> directly.  Numbers in comments refer to the acceptance tests in the design document.
	/// </summary>
	[TestClass]
	public class StorageTests
	{
		private string dataDir;
		private long clock;
		private OpStats stats;

		[TestInitialize]
		public void Init()
		{
			dataDir = Path.Combine(Path.GetTempPath(), "KVStoreTests", Guid.NewGuid().ToString("N"));
			clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			stats = new OpStats(() => clock);
		}
		[TestCleanup]
		public void Cleanup()
		{
			try
			{
				Directory.Delete(dataDir, true);
			}
			catch { }
		}
		private KvStorage OpenStorage()
		{
			KvStorage s = new KvStorage(dataDir, stats, () => clock);
			s.Open();
			return s;
		}
		private static BucketConfig Bucket(string name = "default")
		{
			return new BucketConfig() { name = name };
		}
		/// <summary>
		/// Stores an item the way the public API does: write to a temp file, then commit.
		/// </summary>
		private static PutResult Put(KvStorage storage, BucketConfig bucket, string key, byte[] value, int ttl = 3600)
		{
			string temp = storage.CreateTempFilePath();
			File.WriteAllBytes(temp, value);
			PutResult r = storage.CommitPut(bucket, key, temp, value.Length, ttl, 0);
			if (!r.Ok)
				File.Delete(temp);
			return r;
		}
		private static byte[] Read(KvStorage storage, string bucket, string key)
		{
			using (ItemReader item = storage.OpenLive(bucket, key))
			{
				if (item == null)
					return null;
				MemoryStream ms = new MemoryStream();
				item.Stream.CopyTo(ms);
				return ms.ToArray();
			}
		}

		[TestMethod]
		public void TestCorruptDatabaseRecoversAtStartup()
		{
			// 15. Corrupting kvstore.db and restarting: the service recovers, recreates the database, clears blobs/, increments RecoveryEventCount, and keeps serving.
			BucketConfig b = Bucket();
			string key = TestServer.NewKey();
			using (KvStorage storage = OpenStorage())
			{
				Assert.IsTrue(Put(storage, b, key, new byte[] { 1, 2, 3 }).Ok);
				Assert.AreEqual(0, storage.RecoveryEventCount);
			}
			string dbPath = Path.Combine(dataDir, KvStorage.DatabaseFileName);
			Assert.IsTrue(File.Exists(dbPath));
			byte[] garbage = new byte[64 * 1024];
			new Random(3).NextBytes(garbage);
			File.WriteAllBytes(dbPath, garbage);

			using (KvStorage storage = OpenStorage())
			{
				Assert.AreEqual(1, storage.RecoveryEventCount);
				Assert.AreEqual(clock, storage.LastRecoveryUnixSeconds);
				Assert.IsNull(Read(storage, "default", key));
				Assert.AreEqual(0, Directory.GetFileSystemEntries(storage.BlobDirectory).Length, "blobs/ must be cleared");
				Assert.AreEqual(1, Directory.GetFiles(dataDir, KvStorage.DatabaseFileName + ".corrupt.*").Length);
				// Keeps serving.
				Assert.IsTrue(Put(storage, b, key, new byte[] { 4, 5 }).Ok);
				CollectionAssert.AreEqual(new byte[] { 4, 5 }, Read(storage, "default", key));
			}
			// The counter is persistent.
			using (KvStorage storage = OpenStorage())
			{
				Assert.AreEqual(1, storage.RecoveryEventCount);
				CollectionAssert.AreEqual(new byte[] { 4, 5 }, Read(storage, "default", key));
			}
		}

		[TestMethod]
		public void TestCorruptFileRetention()
		{
			Directory.CreateDirectory(dataDir);
			for (int i = 0; i < 5; i++)
			{
				byte[] garbage = new byte[64 * 1024];
				new Random(i).NextBytes(garbage);
				File.WriteAllBytes(Path.Combine(dataDir, KvStorage.DatabaseFileName), garbage);
				using (KvStorage storage = OpenStorage())
					Assert.AreEqual(i + 1, storage.RecoveryEventCount);
				Thread.Sleep(5); // Distinct timestamps
			}
			Assert.AreEqual(KvStorage.CorruptFilesToKeep, Directory.GetFiles(dataDir, KvStorage.DatabaseFileName + ".corrupt.*").Length);
		}

		[TestMethod]
		public void TestStructuralDamageClassification()
		{
			Assert.IsTrue(KvStorage.IsStructuralDamage(new LiteDB.LiteException(LiteDB.LiteException.INVALID_DATABASE, "x")));
			Assert.IsTrue(KvStorage.IsStructuralDamage(new LiteDB.LiteException(LiteDB.LiteException.INVALID_DATAFILE_STATE, "x")));
			Assert.IsTrue(KvStorage.IsStructuralDamage(new LiteDB.LiteException(LiteDB.LiteException.ENGINE_DISPOSED, "x")));
			Assert.IsFalse(KvStorage.IsStructuralDamage(new LiteDB.LiteException(LiteDB.LiteException.INDEX_DUPLICATE_KEY, "x")));
			Assert.IsFalse(KvStorage.IsStructuralDamage(new IOException("disk")));
			Assert.IsFalse(KvStorage.IsStructuralDamage(new NullReferenceException()), "Not thrown from LiteDB");
		}

		[TestMethod]
		public void TestOrphanSweep()
		{
			// 16. A crash between the blob write and the metadata upsert leaves an orphan that the sweeper reclaims within one cycle.
			using (KvStorage storage = OpenStorage())
			{
				BucketConfig b = Bucket();
				string liveKey = TestServer.NewKey();
				Assert.IsTrue(Put(storage, b, liveKey, new byte[] { 1 }).Ok);

				// Simulate the crash: the value file was moved into place but the metadata was never written.
				string orphanKey = TestServer.NewKey();
				string orphanPath = storage.GetBlobPath("default", orphanKey);
				Directory.CreateDirectory(Path.GetDirectoryName(orphanPath));
				File.WriteAllBytes(orphanPath, new byte[] { 9, 9, 9 });
				// And a crash during an upload.
				string abandonedTemp = storage.CreateTempFilePath();
				File.WriteAllBytes(abandonedTemp, new byte[] { 1 });
				string strayTmp = Path.Combine(Path.GetDirectoryName(orphanPath), "x.tmp");
				File.WriteAllBytes(strayTmp, new byte[] { 1 });

				// Young files are left alone, to avoid racing an upload in progress.
				OrphanSweepResult r = storage.SweepOrphans(600);
				Assert.AreEqual(0, r.filesReclaimed);
				Assert.IsTrue(File.Exists(orphanPath));

				foreach (string path in new string[] { orphanPath, abandonedTemp, strayTmp })
					File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-11));
				r = storage.SweepOrphans(600);
				Assert.AreEqual(3, r.filesReclaimed);
				Assert.IsFalse(File.Exists(orphanPath));
				Assert.IsFalse(File.Exists(abandonedTemp));
				Assert.IsFalse(File.Exists(strayTmp));
				Assert.AreEqual(1, r.blobTreeFiles);
				Assert.AreEqual(1, r.blobTreeBytes);
				CollectionAssert.AreEqual(new byte[] { 1 }, Read(storage, "default", liveKey), "Live items are untouched");

				// Metadata whose file is missing is removed.
				File.Delete(storage.GetBlobPath("default", liveKey));
				r = storage.SweepOrphans(600);
				Assert.AreEqual(1, r.metadataRemoved);
				Assert.IsNull(storage.GetLive("default", liveKey));
				Assert.AreEqual(0, storage.GetUsage("default").Count);
			}
		}

		[TestMethod]
		public void TestChurnKeepsStorageFlat()
		{
			// 17. Sustained put/delete churn leaves the blob tree size flat and the LiteDB file at a stable steady state.
			const int rounds = 4;
			const int opsPerRound = 1500;
			using (KvStorage storage = OpenStorage())
			{
				BucketConfig b = Bucket();
				byte[] value = new byte[2000];
				List<long> dbSizes = new List<long>();
				Random r = new Random(4);
				for (int round = 0; round < rounds; round++)
				{
					List<string> live = new List<string>();
					for (int i = 0; i < opsPerRound; i++)
					{
						string key = TestServer.NewKey();
						Assert.IsTrue(Put(storage, b, key, value, 60 + r.Next(3600)).Ok);
						live.Add(key);
						// Keep roughly 100 items alive.
						if (live.Count > 100)
						{
							int victim = r.Next(live.Count);
							Assert.IsTrue(storage.Delete("default", live[victim]));
							live.RemoveAt(victim);
						}
					}
					foreach (string key in live)
						storage.Delete("default", key);
					storage.Checkpoint();
					dbSizes.Add(storage.GetDatabaseFileBytes());
					OrphanSweepResult sweep = storage.SweepOrphans(600);
					Assert.AreEqual(0, sweep.blobTreeFiles, "Blob tree must be empty after all items are deleted");
					Assert.AreEqual(0, sweep.filesReclaimed, "Churn must not leave orphans");
					Assert.AreEqual(0, storage.GetUsage("default").Count);
				}
				Console.WriteLine("Database file sizes after each round: " + string.Join(", ", dbSizes.Select(s => StringUtil.FormatDiskBytes(s))));
				// LiteDB reuses freed pages, so after the first round the file should not keep growing.
				Assert.IsTrue(dbSizes.Last() <= dbSizes[1] * 1.25 + 65536, "Database kept growing: " + string.Join(", ", dbSizes));
			}
		}

		[TestMethod]
		public void TestBucketRenameAndFlush()
		{
			using (KvStorage storage = OpenStorage())
			{
				BucketConfig b = Bucket("photos");
				string k1 = TestServer.NewKey(), k2 = TestServer.NewKey();
				Assert.IsTrue(Put(storage, b, k1, new byte[] { 1 }).Ok);
				Assert.IsTrue(Put(storage, b, k2, new byte[] { 2, 2 }).Ok);
				storage.RenameBucket("photos", "pictures");
				Assert.IsNull(storage.GetLive("photos", k1));
				CollectionAssert.AreEqual(new byte[] { 1 }, Read(storage, "pictures", k1));
				CollectionAssert.AreEqual(new byte[] { 2, 2 }, Read(storage, "pictures", k2));
				Assert.AreEqual(2, storage.GetUsage("pictures").Count);
				Assert.AreEqual(3, storage.GetUsage("pictures").Bytes);
				Assert.AreEqual(0, storage.GetUsage("photos").Count);

				Assert.AreEqual(2, storage.FlushBucket("pictures"));
				Assert.IsNull(storage.GetLive("pictures", k1));
				Assert.IsFalse(Directory.Exists(Path.Combine(storage.BlobDirectory, "pictures")));

				Assert.IsTrue(Put(storage, Bucket("a"), k1, new byte[] { 1 }).Ok);
				Assert.IsTrue(Put(storage, Bucket("b"), k1, new byte[] { 1 }).Ok);
				Assert.AreEqual(2, storage.FlushAll());
				Assert.AreEqual(0, Directory.GetFileSystemEntries(storage.BlobDirectory).Length);
				storage.Rebuild();
				Assert.IsTrue(Put(storage, Bucket("a"), k1, new byte[] { 7 }).Ok);
				CollectionAssert.AreEqual(new byte[] { 7 }, Read(storage, "a", k1));
			}
		}

		[TestMethod]
		public void TestKeysDifferingOnlyByCaseAreDistinct()
		{
			// Permissive keys are case-sensitive, so the database must compare IDs ordinally (LiteDB's default collation ignores case).
			using (KvStorage storage = OpenStorage())
			{
				BucketConfig b = Bucket();
				Assert.IsTrue(Put(storage, b, "Permissive_Key_Number_1", new byte[] { 1 }).Ok);
				Assert.IsNull(storage.GetLive("default", "permissive_key_number_1"));
				Assert.IsNull(Read(storage, "default", "PERMISSIVE_KEY_NUMBER_1"));
				Assert.IsTrue(Put(storage, b, "permissive_key_number_1", new byte[] { 2 }).Ok);
				CollectionAssert.AreEqual(new byte[] { 1 }, Read(storage, "default", "Permissive_Key_Number_1"));
				CollectionAssert.AreEqual(new byte[] { 2 }, Read(storage, "default", "permissive_key_number_1"));
				Assert.AreEqual(2, storage.GetUsage("default").Count);
				Assert.IsFalse(storage.Delete("default", "PERMISSIVE_KEY_NUMBER_1"));
				Assert.AreEqual(2, storage.GetUsage("default").Count);
			}
		}

		[TestMethod]
		public void TestDatabaseWithDefaultCollationIsConverted()
		{
			Directory.CreateDirectory(dataDir);
			string key = TestServer.NewKey();
			// A database created with LiteDB's default (case-insensitive) collation.
			using (LiteDB.LiteDatabase db = new LiteDB.LiteDatabase(new LiteDB.ConnectionString { Filename = Path.Combine(dataDir, KvStorage.DatabaseFileName), Connection = LiteDB.ConnectionType.Direct }))
			{
				Assert.AreNotEqual(System.Globalization.CompareOptions.Ordinal, db.Collation.SortOptions);
				db.GetCollection<KvMeta>("meta").Insert(new KvMeta() { Id = "default:" + key, Bucket = "default", Size = 1, Created = clock, Expires = clock + 600 });
			}
			using (KvStorage storage = OpenStorage())
			{
				Assert.AreEqual(0, storage.RecoveryEventCount);
				Assert.IsNotNull(storage.GetLive("default", key), "Existing metadata is preserved by the conversion");
			}
			using (LiteDB.LiteDatabase db = new LiteDB.LiteDatabase(new LiteDB.ConnectionString { Filename = Path.Combine(dataDir, KvStorage.DatabaseFileName), Connection = LiteDB.ConnectionType.Direct }))
				Assert.AreEqual(System.Globalization.CompareOptions.Ordinal, db.Collation.SortOptions);
		}

		[TestMethod]
		public void TestUsageSurvivesRestart()
		{
			BucketConfig b = Bucket();
			using (KvStorage storage = OpenStorage())
			{
				Assert.IsTrue(Put(storage, b, TestServer.NewKey(), new byte[10]).Ok);
				Assert.IsTrue(Put(storage, b, TestServer.NewKey(), new byte[20]).Ok);
			}
			using (KvStorage storage = OpenStorage())
			{
				Assert.AreEqual(2, storage.GetUsage("default").Count);
				Assert.AreEqual(30, storage.GetUsage("default").Bytes);
				Dictionary<string, BucketItemStats> stats = storage.ComputeItemStats();
				Assert.AreEqual(2, stats["default"].count);
				Assert.AreEqual(10, stats["default"].minSize);
				Assert.AreEqual(20, stats["default"].maxSize);
				Assert.AreEqual(15, stats["default"].avgSize);
				Assert.AreEqual(2, stats["default"].sizeHistogram[0]);
				Assert.AreEqual(2, stats["default"].expiryHistogram[3], "Expiring in more than an hour");
			}
		}
	}
}
