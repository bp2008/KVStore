using BPUtil;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore.Tests
{
	/// <summary>
	/// Tests of the public API over real HTTP connections.  Numbers in comments refer to the acceptance tests in the design document.
	/// </summary>
	[TestClass]
	public class ApiTests
	{
		private static byte[] RandomBytes(int length)
		{
			byte[] data = new byte[length];
			new Random(length).NextBytes(data);
			return data;
		}

		[TestMethod]
		public async Task TestPutGetRoundTrip5MiB()
		{
			// 1. put then get round-trips a 5 MiB binary payload byte-for-byte.
			using (TestServer ts = new TestServer())
			{
				byte[] data = RandomBytes(5 * 1024 * 1024);
				string key = TestServer.NewKey();
				ApiResult put = await ts.Post("put", new { key, value = Convert.ToBase64String(data) });
				Assert.AreEqual(200, put.Status, put.ToString());
				Assert.IsTrue(put.Ok);
				Assert.AreEqual(data.Length, (long)put.Json["size"]);
				Assert.AreEqual(3600, (int)put.Json["ttl"]);

				ApiResult get = await ts.Post("get", new { key });
				Assert.AreEqual(200, get.Status, get.ToString());
				CollectionAssert.AreEqual(data, Convert.FromBase64String((string)get.Json["value"]));
				Assert.AreEqual(data.Length, (long)get.Json["size"]);
				Assert.AreEqual((long)put.Json["expires"], (long)get.Json["expires"]);

				// Small and empty values, and every remainder mod 3.
				foreach (int len in new int[] { 0, 1, 2, 3, 4, 100 })
				{
					byte[] small = RandomBytes(len);
					string k = TestServer.NewKey();
					Assert.AreEqual(200, (await ts.Post("put", new { key = k, value = Convert.ToBase64String(small) })).Status);
					ApiResult g = await ts.Post("get", new { key = k });
					CollectionAssert.AreEqual(small, Convert.FromBase64String((string)g.Json["value"]));
				}
			}
		}

		[TestMethod]
		public async Task TestTooLarge()
		{
			// 2. A 5 MiB + 1 byte payload returns 413.
			using (TestServer ts = new TestServer())
			{
				byte[] data = RandomBytes(5 * 1024 * 1024 + 1);
				string key = TestServer.NewKey();
				ApiResult put = await ts.Post("put", new { key, value = Convert.ToBase64String(data) });
				Assert.AreEqual(413, put.Status, put.ToString());
				Assert.AreEqual("too_large", put.Error);
				Assert.AreEqual(404, (await ts.Post("get", new { key })).Status);
				Assert.AreEqual(0, Directory.GetFiles(ts.Engine.Storage.TempDirectory).Length, "The partial upload must be deleted");

				// A bucket's own limit applies even when the value arrives before the bucket name.
				ts.UpdateSettings(s => s.GetBucket("photos").maxItemSizeBytes = 1000);
				put = await ts.Post("put", "{\"value\":\"" + Convert.ToBase64String(RandomBytes(1001)) + "\",\"key\":\"" + key + "\",\"bucket\":\"photos\"}");
				Assert.AreEqual(413, put.Status, put.ToString());
				put = await ts.Post("put", "{\"value\":\"" + Convert.ToBase64String(RandomBytes(1000)) + "\",\"key\":\"" + key + "\",\"bucket\":\"photos\"}");
				Assert.AreEqual(200, put.Status, put.ToString());
			}
		}

		[TestMethod]
		public async Task TestLargeBodyWithFormContentType()
		{
			// The body is parsed as JSON regardless of the declared content type.  BPUtil must not pre-read url-encoded bodies (it would buffer them in memory, and refuse those over 2 MiB).
			using (TestServer ts = new TestServer())
			{
				byte[] data = RandomBytes(3 * 1024 * 1024);
				string key = TestServer.NewKey();
				ApiResult put = await ts.Post("put", new { key, value = Convert.ToBase64String(data) }, null, "application/x-www-form-urlencoded");
				Assert.AreEqual(200, put.Status, put.ToString());
				ApiResult get = await ts.Post("get", new { key }, null, "application/x-www-form-urlencoded");
				CollectionAssert.AreEqual(data, Convert.FromBase64String((string)get.Json["value"]));
			}
		}

		[TestMethod]
		public void TestTooLargeContentLengthRejectedWithoutReadingBody()
		{
			// 2. An oversized Content-Length is refused with 413 before the body is read: the response arrives even though the body is never sent.
			using (TestServer ts = new TestServer())
			using (TcpClient client = new TcpClient())
			{
				client.ReceiveTimeout = 5000;
				client.Connect(IPAddress.Loopback, ts.Port);
				NetworkStream s = client.GetStream();
				byte[] head = Encoding.ASCII.GetBytes("POST /v1/put HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/plain\r\nContent-Length: 100000000\r\n\r\n{\"value\":\"AAAA");
				s.Write(head, 0, head.Length);
				Stopwatch sw = Stopwatch.StartNew();
				string response = ReadHttpResponseHead(s);
				Assert.IsTrue(response.StartsWith("HTTP/1.1 413"), response);
				Assert.IsTrue(response.IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) >= 0, response);
				Assert.IsTrue(sw.ElapsedMilliseconds < 3000, "Took " + sw.ElapsedMilliseconds + " ms");
			}
		}
		private static string ReadHttpResponseHead(Stream s)
		{
			StringBuilder sb = new StringBuilder();
			while (!sb.ToString().Contains("\r\n\r\n"))
			{
				int b = s.ReadByte();
				if (b == -1)
					break;
				sb.Append((char)b);
			}
			return sb.ToString();
		}

		[TestMethod]
		public async Task TestExpiredButUnsweptIsNotFound()
		{
			// 3. get on an expired-but-unswept item returns 404.
			using (TestServer ts = new TestServer())
			{
				string key = TestServer.NewKey();
				ApiResult put = await ts.Post("put", new { key, value = "AAAA", ttl = 60 });
				Assert.AreEqual(200, put.Status);
				Assert.AreEqual(200, (await ts.Post("get", new { key })).Status);
				ts.Engine.AdvanceClockForTesting(61);
				ApiResult get = await ts.Post("get", new { key });
				Assert.AreEqual(404, get.Status);
				Assert.AreEqual("not_found", get.Error);
				ApiResult info = await ts.Post("info", new { key });
				Assert.AreEqual(200, info.Status);
				Assert.IsFalse((bool)info.Json["exists"]);
				Assert.AreEqual(1, ts.Engine.Storage.GetUsage("default").Count, "Not swept yet");
				ts.Engine.RunExpirySweep();
				Assert.AreEqual(0, ts.Engine.Storage.GetUsage("default").Count);
				Assert.IsFalse(File.Exists(ts.Engine.Storage.GetBlobPath("default", key)));
			}
		}

		[TestMethod]
		public async Task TestKeyValidation()
		{
			// 4. Keys "settings", "test", "", and a 200-char key are rejected invalid_key in strict mode; a 32-char uppercase key is accepted and normalized.
			using (TestServer ts = new TestServer())
			{
				foreach (string bad in new string[] { "settings", "test", "", new string('a', 200) })
				{
					ApiResult r = await ts.Post("put", new { key = bad, value = "AAAA" });
					Assert.AreEqual(400, r.Status, bad);
					Assert.AreEqual("invalid_key", r.Error, bad);
				}
				Assert.AreEqual("invalid_key", (await ts.Post("put", new { key = 12345, value = "AAAA" })).Error);
				Assert.AreEqual("bad_request", (await ts.Post("put", new { value = "AAAA" })).Error, "Missing key");

				string key = TestServer.NewKey();
				ApiResult put = await ts.Post("put", new { key = key.ToUpperInvariant(), value = "AQID" });
				Assert.AreEqual(200, put.Status);
				ApiResult get = await ts.Post("get", new { key });
				Assert.AreEqual(200, get.Status);
				Assert.AreEqual("AQID", (string)get.Json["value"]);

				// Permissive mode
				string permissiveKey = "My.Settings_Key-2024-abc";
				Assert.AreEqual("invalid_key", (await ts.Post("put", new { key = permissiveKey, value = "AAAA" })).Error);
				ts.UpdateSettings(s => s.permissiveKeys = true);
				Assert.AreEqual(200, (await ts.Post("put", new { key = permissiveKey, value = "AAAA" })).Status);
				Assert.AreEqual(404, (await ts.Post("get", new { key = permissiveKey.ToLowerInvariant() })).Status, "Permissive keys are case-sensitive");
				Assert.AreEqual(200, (await ts.Post("get", new { key = permissiveKey })).Status);
				Assert.AreEqual("invalid_key", (await ts.Post("put", new { key = "settings", value = "AAAA" })).Error, "Still too short");
			}
		}

		[TestMethod]
		public async Task TestBuckets()
		{
			// 5. Photos / PHOTOS / photos resolve to the same bucket; photo! is rejected; nonexistent returns unknown_bucket and does not create a bucket.
			using (TestServer ts = new TestServer())
			{
				string key = TestServer.NewKey();
				Assert.AreEqual(200, (await ts.Post("put", new { bucket = "Photos", key, value = "AQID" })).Status);
				Assert.AreEqual("AQID", (string)(await ts.Post("get", new { bucket = "PHOTOS", key })).Json["value"]);
				Assert.AreEqual("AQID", (string)(await ts.Post("get", new { bucket = "photos", key })).Json["value"]);
				Assert.AreEqual(404, (await ts.Post("get", new { key })).Status, "Not in the default bucket");

				ApiResult bad = await ts.Post("put", new { bucket = "photo!", key, value = "AAAA" });
				Assert.AreEqual(400, bad.Status);
				Assert.AreEqual("invalid_bucket", bad.Error);

				int bucketCount = ts.Settings.buckets.Count;
				ApiResult unknown = await ts.Post("put", new { bucket = "nonexistent", key, value = "AAAA" });
				Assert.AreEqual(404, unknown.Status);
				Assert.AreEqual("unknown_bucket", unknown.Error);
				Assert.AreEqual(bucketCount, ts.Settings.buckets.Count);
				Assert.IsNull(ts.Settings.GetBucket("nonexistent"));
				Assert.IsFalse(Directory.Exists(Path.Combine(ts.Engine.Storage.BlobDirectory, "nonexistent")));

				ApiResult disabled = await ts.Post("get", new { bucket = "disabled", key });
				Assert.AreEqual(503, disabled.Status);
				Assert.AreEqual("bucket_disabled", disabled.Error);

				// Empty or null bucket means the default bucket.
				Assert.AreEqual(200, (await ts.Post("put", new { bucket = "", key, value = "AAAA" })).Status);
				Assert.AreEqual("AAAA", (string)(await ts.Post("get", new { bucket = (string)null, key })).Json["value"]);

				// The public bucket list shows only enabled buckets and no counts, usage, or notes.
				ts.UpdateSettings(s => s.GetBucket("photos").notes = "secret admin note");
				ApiResult list = await ts.Post("buckets", null);
				Assert.AreEqual(200, list.Status, list.ToString());
				Assert.AreEqual("default", (string)list.Json["defaultBucket"]);
				string[] names = list.Json["buckets"].Select(b => (string)b["name"]).ToArray();
				CollectionAssert.AreEquivalent(new string[] { "default", "photos" }, names);
				Assert.IsFalse(list.Text.Contains("secret"));
				JObject photos = (JObject)list.Json["buckets"].First(b => (string)b["name"] == "photos");
				CollectionAssert.AreEquivalent(new string[] { "name", "maxItemSizeBytes", "defaultTtl", "maxTtl" }, photos.Properties().Select(p => p.Name).ToArray());
			}
		}

		[TestMethod]
		public async Task TestSameKeyInTwoBuckets()
		{
			// 6. Same key in two buckets stores two independent values.
			using (TestServer ts = new TestServer())
			{
				string key = TestServer.NewKey();
				Assert.AreEqual(200, (await ts.Post("put", new { key, value = "AQID" })).Status);
				Assert.AreEqual(200, (await ts.Post("put", new { bucket = "photos", key, value = "BAUG" })).Status);
				Assert.AreEqual("AQID", (string)(await ts.Post("get", new { key })).Json["value"]);
				Assert.AreEqual("BAUG", (string)(await ts.Post("get", new { bucket = "photos", key })).Json["value"]);
				Assert.IsTrue((bool)(await ts.Post("del", new { bucket = "photos", key })).Json["deleted"]);
				Assert.AreEqual("AQID", (string)(await ts.Post("get", new { key })).Json["value"]);
				Assert.AreEqual(404, (await ts.Post("get", new { bucket = "photos", key })).Status);
			}
		}

		[TestMethod]
		public async Task TestTtlClamping()
		{
			// 7. A ttl above MaxTtlSeconds is clamped and the response reports the clamped value; a ttl below 60 is raised to 60.
			using (TestServer ts = new TestServer())
			{
				long now = ts.Engine.Now();
				ApiResult high = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA", ttl = 999999 });
				Assert.AreEqual(7200, (int)high.Json["ttl"]);
				Assert.IsTrue(Math.Abs((long)high.Json["expires"] - (now + 7200)) <= 2);
				ApiResult low = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA", ttl = 5 });
				Assert.AreEqual(60, (int)low.Json["ttl"]);
				ApiResult neg = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA", ttl = -100 });
				Assert.AreEqual(60, (int)neg.Json["ttl"]);
				ApiResult none = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA" });
				Assert.AreEqual(3600, (int)none.Json["ttl"]);
				ApiResult bad = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA", ttl = "long" });
				Assert.AreEqual("bad_request", bad.Error);
			}
		}

		[TestMethod]
		public async Task TestCors()
		{
			// 8. CORS: preflight is answered immediately; responses allow any origin and never allow credentials.
			using (TestServer ts = new TestServer())
			{
				HttpRequestMessage preflight = new HttpRequestMessage(HttpMethod.Options, "v1/put");
				preflight.Headers.TryAddWithoutValidation("Origin", "https://bp2008.github.io");
				preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
				preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");
				ApiResult r = await ts.Send(preflight);
				Assert.AreEqual(204, r.Status);
				Assert.AreEqual("*", r.Headers["Access-Control-Allow-Origin"]);
				Assert.AreEqual("POST, OPTIONS", r.Headers["Access-Control-Allow-Methods"]);
				Assert.AreEqual("Content-Type", r.Headers["Access-Control-Allow-Headers"]);
				Assert.AreEqual("86400", r.Headers["Access-Control-Max-Age"]);
				Assert.IsFalse(r.Headers.ContainsKey("Access-Control-Allow-Credentials"));

				// Preflight is answered even for unknown /v1 paths, and consumes no rate limit tokens.
				Assert.AreEqual(204, (await ts.Send(new HttpRequestMessage(HttpMethod.Options, "v1/anything"))).Status);

				foreach (string contentType in new string[] { "text/plain;charset=UTF-8", "application/json", "application/octet-stream", "application/x-www-form-urlencoded" })
				{
					ApiResult put = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA" }, null, contentType);
					Assert.AreEqual(200, put.Status, contentType);
					Assert.AreEqual("*", put.Headers["Access-Control-Allow-Origin"]);
					Assert.AreEqual("no-store", put.Headers["Cache-Control"]);
					Assert.IsFalse(put.Headers.ContainsKey("Access-Control-Allow-Credentials"));
				}
				ApiResult err = await ts.Post("get", new { key = TestServer.NewKey() });
				Assert.AreEqual(404, err.Status);
				Assert.AreEqual("*", err.Headers["Access-Control-Allow-Origin"]);
			}
		}

		[TestMethod]
		public async Task TestWriteRateLimit()
		{
			// 9. Exceeding the write limit returns 429 with a valid Retry-After.
			using (TestServer ts = new TestServer())
			{
				for (int i = 0; i < 10; i++)
					Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() })).Status, "Request " + i);
				ApiResult limited = await ts.Post("put", new { key = TestServer.NewKey(), value = "AAAA" });
				Assert.AreEqual(429, limited.Status);
				Assert.AreEqual("rate_limited", limited.Error);
				Assert.IsTrue(int.TryParse(limited.Headers["Retry-After"], out int retryAfter), limited.Headers["Retry-After"]);
				Assert.IsTrue(retryAfter >= 1 && retryAfter <= 60, "60 per hour refill means a token every 60 seconds; Retry-After was " + retryAfter);
				Assert.AreEqual("Retry-After", limited.Headers["Access-Control-Expose-Headers"]);

				// A different client is unaffected.
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "203.0.113.50")).Status);

				// Reads have their own limit.
				Assert.AreEqual(404, (await ts.Post("get", new { key = TestServer.NewKey() })).Status);
			}
		}

		[TestMethod]
		public async Task TestIPv6Slash64SharesRateLimit()
		{
			// 10. Two addresses in the same IPv6 /64 share a rate-limit bucket (the address comes from CF-Connecting-IP, trusted from loopback).
			using (TestServer ts = new TestServer(s => s.rateLimits.writes.capacity = 2))
			{
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "2001:db8:aa:bb::1")).Status);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "2001:db8:aa:bb:ffff:ffff:ffff:fffe")).Status);
				Assert.AreEqual(429, (await ts.Post("del", new { key = TestServer.NewKey() }, "2001:db8:aa:bb:1234::99")).Status);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "2001:db8:aa:bc::1")).Status, "A different /64");
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "198.51.100.1")).Status);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() }, "198.51.100.2")).Status, "IPv4 addresses are limited individually");
			}
		}

		[TestMethod]
		public async Task TestRateLimitSwapTakesEffectImmediately()
		{
			// 11. Changing a rate limit swaps the dictionary and takes effect on the next request without restart.
			using (TestServer ts = new TestServer(s => s.rateLimits.writes.capacity = 1))
			{
				RateLimiterSet before = ts.Engine.RateLimits;
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);
				Assert.AreEqual(429, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);
				ts.UpdateSettings(s => s.rateLimits.writes.capacity = 3);
				Assert.AreNotSame(before, ts.Engine.RateLimits);
				Assert.AreEqual(3, ts.Engine.RateLimits.Writes.Capacity);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);
				Assert.AreEqual(200, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);
				Assert.AreEqual(429, (await ts.Post("del", new { key = TestServer.NewKey() })).Status);

				// Unrelated settings changes do not reset the rate limiters.
				RateLimiterSet current = ts.Engine.RateLimits;
				ts.UpdateSettings(s => s.operatorName = "test");
				Assert.AreSame(current, ts.Engine.RateLimits);
			}
		}

		[TestMethod]
		public async Task TestByteRateLimit()
		{
			using (TestServer ts = new TestServer(s => s.rateLimits.bytes.capacity = 3000))
			{
				Assert.AreEqual(200, (await ts.Post("put", new { key = TestServer.NewKey(), value = Convert.ToBase64String(RandomBytes(2000)) })).Status);
				ApiResult limited = await ts.Post("put", new { key = TestServer.NewKey(), value = Convert.ToBase64String(RandomBytes(2000)) });
				Assert.AreEqual(429, limited.Status);
				Assert.IsTrue(int.Parse(limited.Headers["Retry-After"]) >= 1);
				Assert.AreEqual(200, (await ts.Post("put", new { key = TestServer.NewKey(), value = Convert.ToBase64String(RandomBytes(900)) })).Status);
			}
		}

		[TestMethod]
		public async Task TestDeleteAndInfo()
		{
			using (TestServer ts = new TestServer())
			{
				string key = TestServer.NewKey();
				Assert.AreEqual(200, (await ts.Post("put", new { key, value = Convert.ToBase64String(RandomBytes(10)), ttl = 600 })).Status);
				ApiResult info = await ts.Post("info", new { key });
				Assert.IsTrue((bool)info.Json["exists"]);
				Assert.AreEqual(10, (long)info.Json["size"]);
				Assert.IsNull(info.Json["value"], "info must not transfer the value");
				ApiResult del = await ts.Post("del", new { key });
				Assert.IsTrue((bool)del.Json["deleted"]);
				del = await ts.Post("del", new { key });
				Assert.AreEqual(200, del.Status);
				Assert.IsFalse((bool)del.Json["deleted"]);
				Assert.IsFalse((bool)(await ts.Post("info", new { key })).Json["exists"]);
				Assert.IsFalse(File.Exists(ts.Engine.Storage.GetBlobPath("default", key)));

				// Overwrite replaces the value unconditionally, and reads never extend lifetime.
				Assert.AreEqual(200, (await ts.Post("put", new { key, value = "AQID", ttl = 600 })).Status);
				long expires = (long)(await ts.Post("put", new { key, value = "BAUG", ttl = 600 })).Json["expires"];
				ts.Engine.AdvanceClockForTesting(100);
				ApiResult get = await ts.Post("get", new { key });
				Assert.AreEqual("BAUG", (string)get.Json["value"]);
				Assert.AreEqual(expires, (long)get.Json["expires"]);
				Assert.AreEqual(1, ts.Engine.Storage.GetUsage("default").Count);
			}
		}

		[TestMethod]
		public async Task TestMalformedRequests()
		{
			using (TestServer ts = new TestServer(s => s.rateLimits.reads.capacity = 1000))
			{
				string key = TestServer.NewKey();
				string[] malformed = new string[]
				{
					"", "not json", "[]", "{", "{\"key\":", "{\"key\":\"" + key + "\"} trailing",
					"{\"key\":\"" + key + "\",\"key\":\"" + key + "\"}", // duplicate member
					"{\"key\":\"" + key + "\",\"nested\":{\"a\":1}}",
					"{\"key\":\"" + key + "\",\"list\":[1]}",
					"{\"key\":\"" + key + "\",\"bucket\":\"default\",}",
				};
				foreach (string body in malformed)
				{
					ApiResult r = await ts.Post("get", body);
					Assert.AreEqual(400, r.Status, body);
					Assert.AreEqual("bad_request", r.Error, body);
					Assert.IsFalse(r.Text.Contains("Exception"), "No exception details in responses");
				}
				// Bad base64
				ApiResult bad = await ts.Post("put", new { key, value = "not base64!" });
				Assert.AreEqual(400, bad.Status);
				Assert.AreEqual("invalid_value", bad.Error);
				Assert.AreEqual("invalid_value", (await ts.Post("put", new { key, value = "AAAAA" })).Error);
				Assert.AreEqual("bad_request", (await ts.Post("put", new { key, value = 5 })).Error);

				// Escaped characters inside the value, unknown members, and whitespace are fine.
				ApiResult ok = await ts.Post("put", "﻿{ \"extra\": \"ignored\", \"n\": -1.5e3, \"t\": true, \"key\": \"" + key + "\",\r\n \"value\": \"AQ\\/\\u0044\" }\r\n");
				Assert.AreEqual(200, ok.Status, ok.ToString());
				Assert.AreEqual("AQ/D", (string)(await ts.Post("get", new { key })).Json["value"]);

				// GET is never allowed for key/value operations.
				ApiResult get = await ts.Send(new HttpRequestMessage(HttpMethod.Get, "v1/get?key=" + key));
				Assert.AreEqual(405, get.Status);
				Assert.AreEqual(404, (await ts.Send(new HttpRequestMessage(HttpMethod.Get, key))).Status);
				Assert.AreEqual(404, (await ts.Post("nothing", new { })).Status);
			}
		}

		[TestMethod]
		public async Task TestQuotaEvictionAndStorageFull()
		{
			using (TestServer ts = new TestServer(s =>
			{
				s.GetBucket("photos").maxItemCount = 3;
				s.rateLimits.writes.capacity = 100;
			}))
			{
				// Items closest to expiration are evicted first.
				string[] keys = Enumerable.Range(0, 4).Select(i => TestServer.NewKey()).ToArray();
				int[] ttls = new int[] { 3000, 600, 2000, 1000 };
				for (int i = 0; i < 4; i++)
					Assert.AreEqual(200, (await ts.Post("put", new { bucket = "photos", key = keys[i], value = "AAAA", ttl = ttls[i] })).Status);
				Assert.AreEqual(3, ts.Engine.Storage.GetUsage("photos").Count);
				Assert.AreEqual(404, (await ts.Post("get", new { bucket = "photos", key = keys[1] })).Status, "The item with the shortest remaining lifetime was evicted");
				foreach (int i in new int[] { 0, 2, 3 })
					Assert.AreEqual(200, (await ts.Post("get", new { bucket = "photos", key = keys[i] })).Status);
				// Overwriting an existing item does not evict anything.
				Assert.AreEqual(200, (await ts.Post("put", new { bucket = "photos", key = keys[3], value = "AQID", ttl = 60 })).Status);
				Assert.AreEqual(200, (await ts.Post("get", new { bucket = "photos", key = keys[0] })).Status);
				Assert.AreEqual(3, ts.Engine.Storage.GetUsage("photos").Count);

				// Byte quota
				ts.UpdateSettings(s => { s.GetBucket("photos").maxTotalBytes = 100; s.GetBucket("photos").maxItemSizeBytes = 1000; });
				ApiResult full = await ts.Post("put", new { bucket = "photos", key = TestServer.NewKey(), value = Convert.ToBase64String(RandomBytes(101)) });
				Assert.AreEqual(503, full.Status);
				Assert.AreEqual("storage_full", full.Error);
				Assert.AreEqual(200, (await ts.Post("put", new { bucket = "photos", key = TestServer.NewKey(), value = Convert.ToBase64String(RandomBytes(100)) })).Status);
				Assert.AreEqual(1, ts.Engine.Storage.GetUsage("photos").Count);
				Assert.AreEqual(100, ts.Engine.Storage.GetUsage("photos").Bytes);
			}
		}

		[TestMethod]
		public async Task TestPhraseHealthAndLandingPage()
		{
			using (TestServer ts = new TestServer())
			{
				ApiResult phrase = await ts.Post("phrase", new { words = 6 });
				Assert.AreEqual(200, phrase.Status);
				string p = (string)phrase.Json["phrase"];
				HashSet<string> words = new HashSet<string>(PhraseGenerator.Words);
				Assert.AreEqual(6, p.Replace("yo-yo", "yoyo").Split('-').Length, p);
				Assert.AreEqual(6, (int)(await ts.Post("phrase", null)).Json["phrase"].ToString().Replace("yo-yo", "yoyo").Split('-').Length, "Default is 6 words");
				Assert.AreEqual(200, (await ts.Post("phrase", new { words = 5 })).Status);
				Assert.AreEqual(200, (await ts.Post("phrase", new { words = 10 })).Status);
				Assert.AreEqual(400, (await ts.Post("phrase", new { words = 4 })).Status);
				Assert.AreEqual(400, (await ts.Post("phrase", new { words = 11 })).Status);
				Assert.AreEqual(400, (await ts.Post("phrase", new { words = 5.5 })).Status);

				ApiResult health = await ts.Send(new HttpRequestMessage(HttpMethod.Get, "v1/health"));
				Assert.AreEqual(200, health.Status);
				Assert.IsTrue(health.Ok);
				Assert.IsNotNull(health.Json["version"]);
				Assert.IsNotNull(health.Json["uptime"]);

				ApiResult landing = await ts.Send(new HttpRequestMessage(HttpMethod.Get, ""));
				Assert.AreEqual(200, landing.Status);
				Assert.AreEqual("noindex, nofollow", landing.Headers["X-Robots-Tag"]);
				Assert.IsTrue(landing.Text.Contains("Terms of Service"));
				ts.UpdateSettings(s => s.abuseContact = "abuse@example.com");
				landing = await ts.Send(new HttpRequestMessage(HttpMethod.Get, ""));
				Assert.IsTrue(landing.Text.Contains("mailto:abuse@example.com"));
				ApiResult robots = await ts.Send(new HttpRequestMessage(HttpMethod.Get, "robots.txt"));
				Assert.AreEqual(200, robots.Status);
				Assert.IsTrue(robots.Text.Contains("Disallow: /"));

				// The Admin Console is not reachable through the public listener.
				foreach (string path in new string[] { "Dashboard/Get", "Config/Get", "CommandLineInterface/ReadConfig" })
				{
					HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, path);
					req.Headers.TryAddWithoutValidation("X-KVStore-CSRF-Protection", "1");
					ApiResult r = await ts.Send(req);
					Assert.AreEqual(405, r.Status, path);
					Assert.IsNull(r.Json, path);
				}
				Assert.AreEqual(404, (await ts.Send(new HttpRequestMessage(HttpMethod.Get, "admin.js"))).Status);
				Assert.AreEqual(404, (await ts.Send(new HttpRequestMessage(HttpMethod.Get, "index.html"))).Status);
			}
		}

		[TestMethod]
		public async Task TestConcurrentLargeUploadsMemory()
		{
			// 20. Peak managed heap stays under 400 MB during 20 concurrent 5 MiB uploads.
			using (TestServer ts = new TestServer(s =>
			{
				s.rateLimits.writes.capacity = 1000;
				s.rateLimits.bytes.capacity = 1024L * 1024 * 1024;
				s.rateLimits.reads.capacity = 1000;
			}))
			{
				// One shared request body (the client side of this test must not dominate the measurement).
				byte[] data = RandomBytes(5 * 1024 * 1024);
				string b64 = Convert.ToBase64String(data);
				string[] keys = Enumerable.Range(0, 20).Select(i => TestServer.NewKey()).ToArray();
				byte[][] bodies = keys.Select(k => Encoding.UTF8.GetBytes("{\"key\":\"" + k + "\",\"value\":\"" + b64 + "\"}")).ToArray();
				b64 = null;
				GC.Collect(2, GCCollectionMode.Forced, true, true);
				long baseline = GC.GetTotalMemory(true);

				long peak = 0;
				bool sampling = true;
				Thread sampler = new Thread(() =>
				{
					while (Volatile.Read(ref sampling))
					{
						long m = GC.GetTotalMemory(false);
						if (m > Interlocked.Read(ref peak))
							Interlocked.Exchange(ref peak, m);
						Thread.Sleep(2);
					}
				});
				sampler.Start();
				try
				{
					Task<ApiResult>[] uploads = bodies.Select(body =>
					{
						HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "v1/put");
						req.Content = new ByteArrayContent(body);
						req.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain;charset=UTF-8");
						return ts.Send(req);
					}).ToArray();
					ApiResult[] results = await Task.WhenAll(uploads);
					foreach (ApiResult r in results)
						Assert.AreEqual(200, r.Status, r.ToString());
				}
				finally
				{
					Volatile.Write(ref sampling, false);
					sampler.Join();
				}
				long bodiesSize = bodies.Sum(b => (long)b.Length);
				long serverPeak = peak - baseline;
				Console.WriteLine("Baseline heap: " + StringUtil.FormatDiskBytes(baseline) + " (includes " + StringUtil.FormatDiskBytes(bodiesSize) + " of test request bodies).  Peak increase during uploads: " + StringUtil.FormatDiskBytes(serverPeak));
				Assert.IsTrue(peak < 400L * 1024 * 1024, "Peak managed heap " + StringUtil.FormatDiskBytes(peak));
				// The server streams values to disk, so its memory use must be far smaller than the data uploaded (20 x ~7 MB, which would be several hundred MB if bodies were buffered and decoded in memory).
				Assert.IsTrue(serverPeak < 100L * 1024 * 1024, "Peak increase " + StringUtil.FormatDiskBytes(serverPeak));
				Assert.AreEqual(20, ts.Engine.Storage.GetUsage("default").Count);
			}
		}
	}
}
