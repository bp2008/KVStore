using BPUtil;
using BPUtil.SimpleHttp;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// <para>The public API web server.  It listens on its own port (loopback by default) and is exposed to the internet only through a Cloudflare Tunnel.</para>
	/// <para>All key/value operations are POST requests with JSON bodies and JSON responses under "/v1/".  The only GET endpoints are "/v1/health", the landing page, and robots.txt.</para>
	/// <para>Client IP addresses are learned from the "CF-Connecting-IP" header, which is trusted only from loopback (the local cloudflared process).  They are used only as keys for the in-memory rate limiters and are never logged.</para>
	/// </summary>
	public class PublicApiServer : HttpServerAsync
	{
		/// <summary>
		/// Timeout for each read of the request body, in milliseconds.
		/// </summary>
		public const int ReadTimeoutMs = 15000;
		/// <summary>
		/// Maximum request body size of requests other than "put", in bytes.
		/// </summary>
		public const long SmallBodyLimit = 16384;
		/// <summary>
		/// Base64 encoding makes a value 4/3 as large; the "put" body limit is the bucket's item size limit times this factor, plus <see cref="JsonOverheadAllowance"/>.
		/// </summary>
		public const double Base64OverheadFactor = 1.4;
		/// <summary>
		/// Allowance for the JSON surrounding a value in a "put" body, in bytes.
		/// </summary>
		public const long JsonOverheadAllowance = 4096;
		private const string jsonContentType = "application/json; charset=utf-8";
		private readonly KvEngine engine;

		/// <summary>
		/// Constructs a PublicApiServer.  Call <see cref="UpdateBindings"/> to start listening.
		/// </summary>
		/// <param name="engine">The engine.</param>
		public PublicApiServer(KvEngine engine) : base(null)
		{
			this.engine = engine;
			// Cloudflare sets CF-Connecting-IP to the client's address and always overwrites any client-supplied value.  X-Forwarded-For and X-Real-IP are not trusted: Cloudflare appends to X-Forwarded-For, whose leftmost entry is client-controlled.
			XRealIPHeader = false;
			XForwardedForHeader = false;
			XForwardedProtoHeader = false;
			CFConnectingIPHeader = true;
			// The body is always parsed as JSON regardless of the declared content type, and is streamed rather than buffered.  BPUtil would otherwise read url-encoded bodies (up to 2 MiB each) into memory before any size or rate limit applies.
			ReadFormBodies = false;
		}

		#region HttpServer overrides
		/// <summary>
		/// Returns true only for loopback addresses, i.e. the cloudflared process running on this machine.
		/// </summary>
		/// <param name="p">HttpProcessor</param>
		/// <param name="remoteIpAddress">Address of the peer that made the TCP connection.</param>
		/// <returns></returns>
		public override bool IsTrustedProxyServer(HttpProcessor p, IPAddress remoteIpAddress)
		{
			return remoteIpAddress != null && IPAddress.IsLoopback(remoteIpAddress);
		}
		/// <summary>
		/// Returns false.  Request logs contain client IP addresses, which KVStore never writes to disk.
		/// </summary>
		/// <returns></returns>
		public override bool shouldLogRequestsToFile()
		{
			return false;
		}
		/// <inheritdoc/>
		public override bool shouldLogSocketBind()
		{
			return true;
		}
		/// <inheritdoc/>
		protected override void stopServer()
		{
		}
		private readonly object updateBindingsLock = new object();
		/// <summary>
		/// Reconfigures the web server to listen on the public API endpoint currently in the settings.
		/// </summary>
		public void UpdateBindings()
		{
			lock (updateBindingsLock)
			{
				Settings s = engine.GetSettings();
				List<Binding> bindings = new List<Binding>();
				if (s.publicHttpPortValid())
				{
					if (IPAddress.TryParse(s.publicIpAddress, out IPAddress ip))
						bindings.Add(new Binding(AllowedConnectionTypes.http, new IPEndPoint(ip, s.publicHttpPort)));
					else
						bindings.AddRange(Binding.AllInterfaces(AllowedConnectionTypes.http, (ushort)s.publicHttpPort));
				}
				SetBindings(bindings.ToArray());
			}
		}
		#endregion

		/// <summary>
		/// State of one request.
		/// </summary>
		private sealed class ApiContext
		{
			public HttpProcessor p;
			public Settings settings;
			public RateLimiterSet rateLimits;
			public string clientKey;
			public JsonRequestReader reader;
			public CancellationToken cancellationToken;
			/// <summary>
			/// True if the request body was read to its end (or there was none), so the connection can be reused.
			/// </summary>
			public bool BodyFullyRead => p.Request.RequestBodyStream == null || (reader != null && reader.ReachedEndOfStream);
		}

		/// <inheritdoc/>
		public override async Task handleRequest(HttpProcessor p, string method, CancellationToken cancellationToken = default)
		{
			string page = p.Request.Page.TrimEnd('/');
			if (page.IEquals("v1") || page.IStartsWith("v1/"))
			{
				engine.Stats.Add(OpCounter.Requests);
				ApiContext c = new ApiContext()
				{
					p = p,
					settings = engine.GetSettings(),
					rateLimits = engine.RateLimits,
					clientKey = ClientIp.GetRateLimitKey(p.RemoteIPAddress),
					cancellationToken = cancellationToken
				};
				string endpoint = page.Length > 3 ? page.Substring(3).ToLowerInvariant() : "";
				try
				{
					await HandleApiRequest(c, method, endpoint).ConfigureAwait(false);
				}
				catch (ApiException ex)
				{
					await SendError(c, ex).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					if (HttpProcessor.IsOrdinaryDisconnectException(ex) || p.Response.ResponseHeaderWritten)
						throw;
					string correlationId = Hex.ToHex(ByteUtil.GenerateRandomBytes(6));
					KVStoreService.ReportError(ex, "Public API internal error (correlation ID " + correlationId + ", endpoint \"" + endpoint + "\").");
					await SendJson(c, "500 Internal Server Error", new { ok = false, error = "internal_error", correlationId }).ConfigureAwait(false);
				}
				finally
				{
					if (c.reader != null)
					{
						engine.Stats.Add(OpCounter.BytesIn, c.reader.BytesRead);
						c.reader.Dispose();
					}
				}
			}
			else
				HandleSiteRequest(p, method, page);
		}

		#region Public API
		private async Task HandleApiRequest(ApiContext c, string method, string endpoint)
		{
			// CORS preflight requests are answered before rate limiting and before reading the body.
			if (method == HttpMethods.OPTIONS)
			{
				c.p.Response.Set(null, null, "204 No Content");
				AddApiHeaders(c.p);
				return;
			}
			if (endpoint == "health")
			{
				if (method != HttpMethods.GET && method != HttpMethods.HEAD)
					throw new ApiException("405 Method Not Allowed", "method_not_allowed");
				engine.Stats.Add(OpCounter.Healths);
				await SendJson(c, "200 OK", new { ok = true, version = Globals.AssemblyVersion, uptime = (long)engine.Uptime.TotalSeconds }).ConfigureAwait(false);
				return;
			}
			Func<ApiContext, Task> handler;
			switch (endpoint)
			{
				case "put": handler = HandlePut; break;
				case "get": handler = HandleGet; break;
				case "del": handler = HandleDel; break;
				case "info": handler = HandleInfo; break;
				case "buckets": handler = HandleBuckets; break;
				case "phrase": handler = HandlePhrase; break;
				default: throw new ApiException("404 Not Found", "unknown_endpoint");
			}
			if (method != HttpMethods.POST)
				throw new ApiException("405 Method Not Allowed", "method_not_allowed");
			await handler(c).ConfigureAwait(false);
		}

		/// <summary>
		/// POST /v1/put { "bucket": "default", "key": "&lt;key&gt;", "value": "&lt;base64&gt;", "ttl": 3600 }
		/// </summary>
		private async Task HandlePut(ApiContext c)
		{
			engine.Stats.Add(OpCounter.Puts);
			Settings s = c.settings;
			// The bucket is named inside the body, so the body size limit that can be enforced before reading it is based on the largest enabled bucket.  The actual bucket's limit is enforced while the value is decoded.
			long maxItemSize = s.buckets.Where(b => b.enabled).Select(b => b.maxItemSizeBytes).DefaultIfEmpty(0).Max();
			long maxBody = (long)(maxItemSize * Base64OverheadFactor) + JsonOverheadAllowance;
			long? contentLength = c.p.Request.ContentLength;
			if (contentLength > maxBody)
				throw ApiException.TooLarge();

			Limit(c.rateLimits.Writes, c.clientKey, 1);
			if (contentLength.HasValue)
			{
				// Refuse early, without reading the body, if the client clearly lacks the byte allowance for a value of this size.
				double estimatedSize = Math.Min(Math.Max(0, contentLength.Value - JsonOverheadAllowance) * 3.0 / 4.0, c.rateLimits.Bytes.Capacity);
				int retryAfter = c.rateLimits.Bytes.CheckAvailable(c.clientKey, estimatedSize);
				if (retryAfter > 0)
					throw ApiException.RateLimited(retryAfter);
			}

			BucketConfig bucket = null;
			string key = null;
			double? ttl = null;
			bool haveValue = false;
			string tempPath = engine.Storage.CreateTempFilePath();
			bool committed = false;
			Base64FileSink sink = new Base64FileSink(tempPath, maxItemSize, c.cancellationToken);
			try
			{
				c.reader = new JsonRequestReader(c.p.Request.RequestBodyStream, maxBody, ReadTimeoutMs, c.cancellationToken);
				// Members are validated as soon as they are read, so a bad bucket or key is refused without reading the rest of the body.
				await c.reader.ReadObjectAsync("value", sink, (name, v) =>
				{
					switch (name)
					{
						case "bucket":
							bucket = ResolveBucket(s, v);
							sink.Limit = bucket.maxItemSizeBytes;
							break;
						case "key":
							key = NormalizeKey(s, v);
							break;
						case "ttl":
							if (v.Type == JsonScalarType.Number)
								ttl = v.NumberValue;
							else if (v.Type != JsonScalarType.Null)
								throw ApiException.BadRequest();
							break;
						case "value":
							if (v.Type != JsonScalarType.StreamedString)
								throw ApiException.BadRequest();
							haveValue = true;
							break;
					}
				}).ConfigureAwait(false);
				if (bucket == null)
				{
					bucket = ResolveBucket(s, null);
					sink.Limit = bucket.maxItemSizeBytes;
				}
				if (key == null || !haveValue)
					throw ApiException.BadRequest();

				long size = sink.BytesWritten;
				if (size > 0)
					Limit(c.rateLimits.Bytes, c.clientKey, size);
				int ttlApplied = bucket.ApplyTtl(ttl);
				PutResult result = engine.Storage.CommitPut(bucket, key, tempPath, size, ttlApplied, s.maintenance.minFreeDiskBytes);
				if (!result.Ok)
					throw ApiException.StorageFull();
				committed = true;
				await SendJson(c, "200 OK", new { ok = true, expires = result.Expires, ttl = ttlApplied, size }).ConfigureAwait(false);
			}
			finally
			{
				sink.Dispose();
				if (!committed)
				{
					try
					{
						File.Delete(tempPath);
					}
					catch (Exception ex)
					{
						KVStoreService.ReportError(ex, "Unable to delete temporary file \"" + tempPath + "\".  The orphan sweep will delete it later.");
					}
				}
			}
		}
		/// <summary>
		/// POST /v1/get { "bucket": "default", "key": "&lt;key&gt;" }
		/// </summary>
		private async Task HandleGet(ApiContext c)
		{
			engine.Stats.Add(OpCounter.Gets);
			Limit(c.rateLimits.Reads, c.clientKey, 1);
			(BucketConfig bucket, string key) = await ReadBucketAndKey(c).ConfigureAwait(false);
			using (ItemReader item = engine.Storage.OpenLive(bucket.name, key))
			{
				if (item == null)
					throw ApiException.NotFound();
				await SendValue(c, item).ConfigureAwait(false);
			}
		}
		/// <summary>
		/// POST /v1/del { "bucket": "default", "key": "&lt;key&gt;" }
		/// </summary>
		private async Task HandleDel(ApiContext c)
		{
			engine.Stats.Add(OpCounter.Dels);
			Limit(c.rateLimits.Writes, c.clientKey, 1);
			(BucketConfig bucket, string key) = await ReadBucketAndKey(c).ConfigureAwait(false);
			bool deleted = engine.Storage.Delete(bucket.name, key);
			await SendJson(c, "200 OK", new { ok = true, deleted }).ConfigureAwait(false);
		}
		/// <summary>
		/// POST /v1/info { "bucket": "default", "key": "&lt;key&gt;" }
		/// </summary>
		private async Task HandleInfo(ApiContext c)
		{
			engine.Stats.Add(OpCounter.Infos);
			Limit(c.rateLimits.Reads, c.clientKey, 1);
			(BucketConfig bucket, string key) = await ReadBucketAndKey(c).ConfigureAwait(false);
			KvMeta m = engine.Storage.GetLive(bucket.name, key);
			if (m == null)
				await SendJson(c, "200 OK", new { ok = true, exists = false }).ConfigureAwait(false);
			else
				await SendJson(c, "200 OK", new { ok = true, exists = true, expires = m.Expires, size = m.Size }).ConfigureAwait(false);
		}
		/// <summary>
		/// POST /v1/buckets {}
		/// </summary>
		private async Task HandleBuckets(ApiContext c)
		{
			engine.Stats.Add(OpCounter.BucketLists);
			c.reader = new JsonRequestReader(c.p.Request.RequestBodyStream, SmallBodyLimit, ReadTimeoutMs, c.cancellationToken);
			await c.reader.ReadObjectAsync(null, null, null).ConfigureAwait(false);
			Settings s = c.settings;
			var buckets = s.buckets
				.Where(b => b.enabled)
				.Select(b => new { name = b.name, maxItemSizeBytes = b.maxItemSizeBytes, defaultTtl = b.defaultTtlSeconds, maxTtl = b.maxTtlSeconds })
				.ToArray();
			await SendJson(c, "200 OK", new { ok = true, defaultBucket = s.defaultBucketName, buckets }).ConfigureAwait(false);
		}
		/// <summary>
		/// POST /v1/phrase { "words": 6 }
		/// </summary>
		private async Task HandlePhrase(ApiContext c)
		{
			engine.Stats.Add(OpCounter.Phrases);
			int words = PhraseGenerator.DefaultWords;
			c.reader = new JsonRequestReader(c.p.Request.RequestBodyStream, SmallBodyLimit, ReadTimeoutMs, c.cancellationToken);
			await c.reader.ReadObjectAsync(null, null, (name, v) =>
			{
				if (name == "words")
				{
					if (v.Type == JsonScalarType.Null)
						return;
					if (v.Type != JsonScalarType.Number || v.NumberValue != Math.Floor(v.NumberValue) || v.NumberValue < PhraseGenerator.MinWords || v.NumberValue > PhraseGenerator.MaxWords)
						throw ApiException.BadRequest();
					words = (int)v.NumberValue;
				}
			}).ConfigureAwait(false);
			await SendJson(c, "200 OK", new { ok = true, phrase = PhraseGenerator.Generate(words) }).ConfigureAwait(false);
		}
		#endregion

		#region Helpers
		/// <summary>
		/// Consumes tokens from a rate limiter, throwing "429 rate_limited" if they are not available.
		/// </summary>
		private static void Limit(ClientRateLimiter limiter, string clientKey, double tokens)
		{
			int retryAfter = limiter.TryConsumeOrGetRetryAfter(clientKey, tokens);
			if (retryAfter > 0)
				throw ApiException.RateLimited(retryAfter);
		}
		/// <summary>
		/// Reads a request body containing "bucket" (optional) and "key".
		/// </summary>
		private async Task<(BucketConfig, string)> ReadBucketAndKey(ApiContext c)
		{
			BucketConfig bucket = null;
			string key = null;
			c.reader = new JsonRequestReader(c.p.Request.RequestBodyStream, SmallBodyLimit, ReadTimeoutMs, c.cancellationToken);
			await c.reader.ReadObjectAsync(null, null, (name, v) =>
			{
				if (name == "bucket")
					bucket = ResolveBucket(c.settings, v);
				else if (name == "key")
					key = NormalizeKey(c.settings, v);
			}).ConfigureAwait(false);
			if (bucket == null)
				bucket = ResolveBucket(c.settings, null);
			if (key == null)
				throw ApiException.BadRequest();
			return (bucket, key);
		}
		/// <summary>
		/// Returns the bucket named by the "bucket" member (or the default bucket if the member is absent, null, or empty).  Throws if the bucket is invalid, unknown, or disabled.  Buckets are never created by this method.
		/// </summary>
		/// <param name="s">Settings</param>
		/// <param name="v">Value of the "bucket" member, or null if absent.</param>
		/// <returns></returns>
		private static BucketConfig ResolveBucket(Settings s, JsonScalar v)
		{
			BucketConfig bucket;
			if (v == null || v.Type == JsonScalarType.Null || (v.Type == JsonScalarType.String && v.StringValue == ""))
				bucket = s.GetDefaultBucket();
			else
			{
				if (v.Type != JsonScalarType.String || !KvNames.TryNormalizeBucketName(v.StringValue, out string name))
					throw ApiException.InvalidBucket();
				bucket = s.GetBucket(name);
			}
			if (bucket == null)
				throw ApiException.UnknownBucket();
			if (!bucket.enabled)
				throw ApiException.BucketDisabled();
			return bucket;
		}
		/// <summary>
		/// Validates and normalizes the "key" member.
		/// </summary>
		private static string NormalizeKey(Settings s, JsonScalar v)
		{
			if (v.Type != JsonScalarType.String || !KvNames.TryNormalizeKey(v.StringValue, s.permissiveKeys, out string key))
				throw ApiException.InvalidKey();
			return key;
		}
		/// <summary>
		/// Adds the headers that every public API response carries.
		/// </summary>
		private static void AddApiHeaders(HttpProcessor p)
		{
			HttpHeaderCollection h = p.Response.Headers;
			h["Access-Control-Allow-Origin"] = "*";
			h["Access-Control-Allow-Methods"] = "POST, OPTIONS";
			h["Access-Control-Allow-Headers"] = "Content-Type";
			h["Access-Control-Max-Age"] = "86400";
			h["Access-Control-Expose-Headers"] = "Retry-After";
			h["Cache-Control"] = "no-store";
			h["X-Content-Type-Options"] = "nosniff";
			h["X-Robots-Tag"] = "noindex, nofollow";
		}
		private async Task SendJson(ApiContext c, string httpStatus, object obj, int retryAfterSeconds = 0)
		{
			byte[] body = ByteUtil.Utf8NoBOM.GetBytes(JsonConvert.SerializeObject(obj));
			c.p.Response.FullResponseBytes(body, jsonContentType, httpStatus);
			AddApiHeaders(c.p);
			if (retryAfterSeconds > 0)
				c.p.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
			CountStatus(httpStatus);
			engine.Stats.Add(OpCounter.BytesOut, body.Length);
			if (!c.BodyFullyRead)
			{
				// The rest of the request body will not be read, so the connection can not be reused.  Write the response now, before BPUtil discovers the unread body.
				c.p.Response.PreventKeepalive();
				await c.p.Response.FinishAsync(c.cancellationToken).ConfigureAwait(false);
			}
		}
		private Task SendError(ApiContext c, ApiException ex)
		{
			return SendJson(c, ex.HttpStatus, new { ok = false, error = ex.ErrorCode }, ex.RetryAfterSeconds);
		}
		private void CountStatus(string httpStatus)
		{
			int code = NumberUtil.FirstInt(httpStatus) ?? 0;
			if (code == 400)
				engine.Stats.Add(OpCounter.Status400);
			else if (code == 404)
				engine.Stats.Add(OpCounter.Status404);
			else if (code == 413)
				engine.Stats.Add(OpCounter.Status413);
			else if (code == 429)
				engine.Stats.Add(OpCounter.Status429);
			else if (code == 503)
				engine.Stats.Add(OpCounter.Status503);
			else if (code >= 500)
				engine.Stats.Add(OpCounter.Status5xx);
		}
		/// <summary>
		/// Writes the "get" response, streaming the value from disk and base64-encoding it incrementally so it is never held in memory.
		/// </summary>
		private async Task SendValue(ApiContext c, ItemReader item)
		{
			const int inputChunk = 49152; // Divisible by 3, so only the final chunk produces padding.
			long size = item.Stream.Length;
			byte[] prefix = ByteUtil.Utf8NoBOM.GetBytes("{\"ok\":true,\"expires\":" + item.Meta.Expires + ",\"size\":" + size + ",\"value\":\"");
			byte[] suffix = ByteUtil.Utf8NoBOM.GetBytes("\"}");
			long contentLength = prefix.Length + ((size + 2) / 3) * 4 + suffix.Length;

			HttpProcessor p = c.p;
			p.Response.Set(jsonContentType, null, "200 OK");
			p.Response.ContentLength = contentLength;
			AddApiHeaders(p);
			engine.Stats.Add(OpCounter.BytesOut, contentLength);

			byte[] inBuf = ArrayPool<byte>.Shared.Rent(inputChunk);
			byte[] outBuf = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(inputChunk));
			try
			{
				Stream rs = await p.Response.GetResponseStreamAsync(c.cancellationToken).ConfigureAwait(false);
				await rs.WriteAsync(prefix, 0, prefix.Length, c.cancellationToken).ConfigureAwait(false);
				long remaining = size;
				while (remaining > 0)
				{
					int want = (int)Math.Min(inputChunk, remaining);
					int got = 0;
					while (got < want)
					{
						int n = await item.Stream.ReadAsync(inBuf, got, want - got, c.cancellationToken).ConfigureAwait(false);
						if (n <= 0)
							throw new IOException("Value file ended unexpectedly.");
						got += n;
					}
					remaining -= got;
					Base64.EncodeToUtf8(new ReadOnlySpan<byte>(inBuf, 0, got), outBuf, out int consumed, out int written, remaining == 0);
					await rs.WriteAsync(outBuf, 0, written, c.cancellationToken).ConfigureAwait(false);
				}
				await rs.WriteAsync(suffix, 0, suffix.Length, c.cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(inBuf);
				ArrayPool<byte>.Shared.Return(outBuf);
			}
		}
		#endregion

		#region Landing page
		private void HandleSiteRequest(HttpProcessor p, string method, string page)
		{
			if (method != HttpMethods.GET && method != HttpMethods.HEAD)
			{
				p.Response.Simple("405 Method Not Allowed");
				p.Response.Headers["Allow"] = "GET, HEAD";
			}
			else if (page == "")
				p.Response.FullResponseUTF8(LandingPage.GetHtml(engine.GetSettings()), "text/html; charset=utf-8");
			else if (page.IEquals("robots.txt"))
				p.Response.FullResponseUTF8("User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8");
			else
				p.Response.Simple("404 Not Found");
			HttpHeaderCollection h = p.Response.Headers;
			h["Cache-Control"] = "no-store";
			h["X-Content-Type-Options"] = "nosniff";
			h["X-Robots-Tag"] = "noindex, nofollow";
			h["X-Frame-Options"] = "DENY";
			h["Referrer-Policy"] = "no-referrer";
			h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
		}
		#endregion
	}
}
