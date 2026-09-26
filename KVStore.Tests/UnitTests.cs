using BPUtil;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore.Tests
{
	[TestClass]
	public class UnitTests
	{
		[TestMethod]
		public void TestBase32Rfc4648Vectors()
		{
			// RFC 4648 section 10, lower case and unpadded.
			string[][] vectors = new string[][]
			{
				new string[] { "", "" },
				new string[] { "f", "my" },
				new string[] { "fo", "mzxq" },
				new string[] { "foo", "mzxw6" },
				new string[] { "foob", "mzxw6yq" },
				new string[] { "fooba", "mzxw6ytb" },
				new string[] { "foobar", "mzxw6ytboi" },
			};
			foreach (string[] v in vectors)
			{
				Assert.AreEqual(v[1], Base32.Encode(Encoding.ASCII.GetBytes(v[0])));
				Assert.IsTrue(Base32.TryDecode(v[1], out byte[] decoded));
				Assert.AreEqual(v[0], Encoding.ASCII.GetString(decoded));
				Assert.IsTrue(Base32.TryDecode(v[1].ToUpperInvariant(), out decoded), "Decoding is case-insensitive");
				Assert.AreEqual(v[0], Encoding.ASCII.GetString(decoded));
			}
			Assert.IsFalse(Base32.TryDecode("m", out _), "Invalid length");
			Assert.IsFalse(Base32.TryDecode("mzx", out _), "Invalid length");
			Assert.IsFalse(Base32.TryDecode("mzxw6y", out _), "Invalid length");
			Assert.IsFalse(Base32.TryDecode("mz", out _), "Non-zero trailing bits");
			Assert.IsFalse(Base32.TryDecode("mzxw1", out _), "1 is not in the alphabet");
			Assert.IsFalse(Base32.TryDecode("mzxw6===", out _), "Padding is not accepted");

			Random r = new Random(1);
			for (int len = 0; len < 64; len++)
			{
				byte[] data = new byte[len];
				r.NextBytes(data);
				string encoded = Base32.Encode(data);
				Assert.IsTrue(encoded.All(c => Base32.Alphabet.Contains(c)));
				Assert.IsTrue(Base32.TryDecode(encoded, out byte[] roundTrip));
				CollectionAssert.AreEqual(data, roundTrip);
			}
			Assert.AreEqual(32, Base32.Encode(new byte[20]).Length, "160 bits encode to 32 characters");
		}

		[TestMethod]
		public void TestKeyValidation()
		{
			// Acceptance test 4
			foreach (string bad in new string[] { "settings", "test", "", null, new string('a', 200), new string('a', 31), new string('a', 33), "abcdefghijklmnopqrstuvwxyz23456!", "abcdefghijklmnopqrstuvwxyz234561" })
				Assert.IsFalse(KvNames.TryNormalizeKey(bad, false, out _), "Strict mode should reject \"" + bad + "\"");
			Assert.IsTrue(KvNames.TryNormalizeKey("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", false, out string key));
			Assert.AreEqual("abcdefghijklmnopqrstuvwxyz234567", key);

			// Permissive mode
			Assert.IsTrue(KvNames.TryNormalizeKey("My_Key-With.Dots-0123", true, out key));
			Assert.AreEqual("My_Key-With.Dots-0123", key, "Permissive keys are case-sensitive");
			Assert.IsTrue(KvNames.TryNormalizeKey("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", true, out key));
			Assert.AreEqual("abcdefghijklmnopqrstuvwxyz234567", key, "Strict-format keys are normalized even in permissive mode");
			Assert.IsFalse(KvNames.TryNormalizeKey("settings", true, out _), "Too short even for permissive mode");
			Assert.IsFalse(KvNames.TryNormalizeKey(new string('a', 129), true, out _));
			Assert.IsFalse(KvNames.TryNormalizeKey("has space in the key ok", true, out _));
			Assert.IsFalse(KvNames.TryNormalizeKey("slash/is/not/allowed/here", true, out _));
		}

		[TestMethod]
		public void TestBlobFileNames()
		{
			string strict = "abcdefghijklmnopqrstuvwxyz234567";
			Assert.AreEqual(strict, KvNames.GetFileStem(strict));
			Assert.AreEqual("ab", KvNames.GetShardDirectoryName(KvNames.GetFileStem(strict)));

			// Permissive keys that could escape the directory or collide on case-insensitive file systems get safe, distinct file names.
			string[] permissive = new string[] { "....................", "..__..__..__..__..__", "AAAAAAAAAAAAAAAAAAAA", "aaaaaaaaaaaaaaaaaaaa" };
			HashSet<string> stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string k in permissive)
			{
				string stem = KvNames.GetFileStem(k);
				Assert.IsTrue(stem.StartsWith("p_"));
				Assert.IsTrue(stem.Substring(2).All(c => Base32.Alphabet.Contains(c)), stem);
				Assert.IsTrue(stems.Add(stem), "File names must not collide, even case-insensitively");
				Assert.IsTrue(KvNames.TryGetKeyFromFileStem(stem, out string roundTrip));
				Assert.AreEqual(k, roundTrip);
				Assert.IsFalse(KvNames.GetShardDirectoryName(stem).Contains('.'));
			}
			Assert.IsTrue(KvNames.TryGetKeyFromFileStem(strict, out string strictRoundTrip));
			Assert.AreEqual(strict, strictRoundTrip);
			Assert.IsFalse(KvNames.TryGetKeyFromFileStem("garbage", out _));
			Assert.IsFalse(KvNames.TryGetKeyFromFileStem("p_" + Base32.Encode(Encoding.UTF8.GetBytes(strict)), out _), "A strict key is never stored under a p_ name");
		}

		[TestMethod]
		public void TestBucketNames()
		{
			// Acceptance test 5 (format part)
			Assert.IsTrue(KvNames.TryNormalizeBucketName("Photos", out string a));
			Assert.IsTrue(KvNames.TryNormalizeBucketName("PHOTOS", out string b));
			Assert.IsTrue(KvNames.TryNormalizeBucketName("photos", out string c));
			Assert.AreEqual("photos", a);
			Assert.AreEqual(a, b);
			Assert.AreEqual(a, c);
			Assert.IsFalse(KvNames.TryNormalizeBucketName("photo!", out _));
			Assert.IsFalse(KvNames.TryNormalizeBucketName("photo1", out _), "1 is not base32");
			Assert.IsFalse(KvNames.TryNormalizeBucketName("", out _));
			Assert.IsFalse(KvNames.TryNormalizeBucketName(new string('a', 33), out _));
			Assert.IsTrue(KvNames.TryNormalizeBucketName(new string('a', 32), out _));
		}

		[TestMethod]
		public void TestRateLimitKeys()
		{
			// Acceptance test 10 (key part): two addresses in the same IPv6 /64 share a key.
			string a = ClientIp.GetRateLimitKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
			string b = ClientIp.GetRateLimitKey(IPAddress.Parse("2001:db8:1:2::1"));
			string other = ClientIp.GetRateLimitKey(IPAddress.Parse("2001:db8:1:3::1"));
			Assert.AreEqual(a, b);
			Assert.AreNotEqual(a, other);
			Assert.AreEqual("2001:db8:1:2::/64", a);
			Assert.AreEqual("203.0.113.5", ClientIp.GetRateLimitKey(IPAddress.Parse("203.0.113.5")));
			Assert.AreEqual("203.0.113.5", ClientIp.GetRateLimitKey(IPAddress.Parse("::ffff:203.0.113.5")), "IPv4-mapped addresses are treated as IPv4");
		}

		[TestMethod]
		public void TestIpScrubber()
		{
			// Each sample is followed by the address text that must not survive scrubbing.
			string[][] samples = new string[][]
			{
				new string[] { "203.0.113.9 GET /v1/get - ", "203.0.113.9" },
				new string[] { "Connection from [2001:db8::42]:443 failed", "2001:db8::42" },
				new string[] { "client 2001:0db8:0000:0000:0000:ff00:0042:8329 reset", "2001:0db8" },
				new string[] { "mapped ::ffff:198.51.100.7 ok", "198.51.100.7" },
				new string[] { "loopback ::1 and 127.0.0.1:8080", "127.0.0.1", "::1" },
				new string[] { "zone fe80::1%eth0 here", "fe80::1" },
				new string[] { "prefix 2001:db8:1:2::/64 limited", "2001:db8:1:2::" },
			};
			foreach (string[] sample in samples)
			{
				string scrubbed = IpScrubber.Scrub(sample[0]);
				Assert.IsTrue(scrubbed.Contains(IpScrubber.Replacement), scrubbed);
				foreach (string leak in sample.Skip(1))
					Assert.IsFalse(scrubbed.Contains(leak), "Leaked \"" + leak + "\" in: " + scrubbed);
			}
			// Things that are not IP addresses are left alone.
			string benign = "at 12:34:56 in KVStore, Version=1.0.0.0, file.cs:line 42, v1.2.3.4";
			Assert.AreEqual(benign, IpScrubber.Scrub(benign));
		}

		[TestMethod]
		public void TestBase64StreamDecoder()
		{
			Random r = new Random(2);
			for (int len = 0; len < 300; len++)
			{
				byte[] data = new byte[len];
				r.NextBytes(data);
				string b64 = Convert.ToBase64String(data);
				foreach (string variant in new string[] { b64, b64.TrimEnd('='), b64.Replace('+', '-').Replace('/', '_'), InsertWhitespace(b64, r) })
				{
					byte[] input = Encoding.ASCII.GetBytes(variant);
					Base64StreamDecoder dec = new Base64StreamDecoder();
					MemoryStream output = new MemoryStream();
					int pos = 0;
					while (pos < input.Length)
					{
						int chunk = Math.Min(input.Length - pos, r.Next(1, 20));
						byte[] outBuf = new byte[Base64StreamDecoder.GetMaxOutputLength(chunk)];
						int n = dec.Decode(new ReadOnlySpan<byte>(input, pos, chunk), outBuf);
						output.Write(outBuf, 0, n);
						pos += chunk;
					}
					byte[] fin = new byte[2];
					output.Write(fin, 0, dec.Finish(fin));
					CollectionAssert.AreEqual(data, output.ToArray(), "Variant: " + variant);
					Assert.AreEqual(len, dec.BytesDecoded);
				}
			}
			foreach (string bad in new string[] { "A", "AAAAA", "AA=A", "A===", "AAA==", "AA*A", "AA==AA" })
			{
				Base64StreamDecoder dec = new Base64StreamDecoder();
				byte[] outBuf = new byte[16];
				Assert.ThrowsException<FormatException>(() =>
				{
					dec.Decode(Encoding.ASCII.GetBytes(bad), outBuf);
					dec.Finish(outBuf);
				}, "Should reject \"" + bad + "\"");
			}
		}
		private static string InsertWhitespace(string s, Random r)
		{
			StringBuilder sb = new StringBuilder();
			foreach (char c in s)
			{
				sb.Append(c);
				if (r.Next(10) == 0)
					sb.Append("\r\n");
			}
			return sb.ToString();
		}

		[TestMethod]
		public void TestClientRateLimiter()
		{
			ClientRateLimiter l = new ClientRateLimiter(3, 1, 2);
			Assert.AreEqual(0, l.TryConsumeOrGetRetryAfter("a", 1));
			Assert.AreEqual(0, l.TryConsumeOrGetRetryAfter("a", 2));
			int retryAfter = l.TryConsumeOrGetRetryAfter("a", 1);
			Assert.IsTrue(retryAfter >= 1 && retryAfter <= 2, "Retry-After: " + retryAfter);
			Assert.AreEqual(0, l.TryConsumeOrGetRetryAfter("b", 1));
			// The ceiling is 2 tracked clients, so a third client is refused rather than allocated.
			Assert.AreEqual(ClientRateLimiter.TableFullRetryAfterSeconds, l.TryConsumeOrGetRetryAfter("c", 1));
			Assert.AreEqual(2, l.NumberOfBuckets);
			// A request larger than the capacity can never succeed.
			Assert.IsTrue(l.TryConsumeOrGetRetryAfter("b", 10) > 0);
			Assert.AreEqual(ClientRateLimiter.TableFullRetryAfterSeconds, l.CheckAvailable("zzz", 3));
			Assert.AreEqual(2, l.NumberOfBuckets);

			// Checking availability never allocates a bucket.
			ClientRateLimiter l2 = new ClientRateLimiter(3, 1, 100);
			Assert.AreEqual(0, l2.CheckAvailable("x", 3));
			Assert.IsTrue(l2.CheckAvailable("x", 4) > 0);
			Assert.AreEqual(0, l2.NumberOfBuckets);
		}

		[TestMethod]
		public void TestPhraseGenerator()
		{
			Assert.AreEqual(1296, PhraseGenerator.Words.Count);
			Assert.AreEqual(1296, PhraseGenerator.Words.Distinct().Count());
			HashSet<string> words = new HashSet<string>(PhraseGenerator.Words);
			string phrase = PhraseGenerator.Generate(6);
			// "yo-yo" is the only word containing a hyphen.
			Assert.IsTrue(phrase.Replace("yo-yo", "yoyo").Split('-').Length == 6, phrase);
			Assert.AreNotEqual(phrase, PhraseGenerator.Generate(6));
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => PhraseGenerator.Generate(4));
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => PhraseGenerator.Generate(11));
		}

		[TestMethod]
		public void TestSettingsValidation()
		{
			Settings s = new Settings();
			s.buckets.Add(new BucketConfig() { name = "Photos", defaultTtlSeconds = 10, maxTtlSeconds = 5 });
			KVStoreService.ValidateSettings(s);
			Assert.AreEqual("photos", s.buckets.First(b => b.name == "photos").name);
			Assert.IsNotNull(s.GetDefaultBucket(), "The default bucket always exists");
			Assert.AreEqual(BucketConfig.MinTtlSeconds, s.GetBucket("photos").maxTtlSeconds);
			Assert.AreEqual(BucketConfig.MinTtlSeconds, s.GetBucket("photos").defaultTtlSeconds);

			Settings dup = new Settings();
			dup.buckets.Add(new BucketConfig() { name = "abc" });
			dup.buckets.Add(new BucketConfig() { name = "ABC" });
			Assert.ThrowsException<Exception>(() => KVStoreService.ValidateSettings(dup));

			Settings bad = new Settings();
			bad.buckets.Add(new BucketConfig() { name = "bad!" });
			Assert.ThrowsException<Exception>(() => KVStoreService.ValidateSettings(bad));

			Settings disabledDefault = new Settings();
			disabledDefault.buckets.Add(new BucketConfig() { name = "default", enabled = false });
			KVStoreService.ValidateSettings(disabledDefault);
			Assert.IsTrue(disabledDefault.GetDefaultBucket().enabled, "The default bucket can not be disabled");

			// Settings survive a JSON round trip without duplicating list items.
			Settings clone = Newtonsoft.Json.JsonConvert.DeserializeObject<Settings>(Newtonsoft.Json.JsonConvert.SerializeObject(s));
			Assert.AreEqual(s.buckets.Count, clone.buckets.Count);
		}
	}
}
