using BPUtil;
using BPUtil.SimpleHttp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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
	/// A KVStore engine and public API server running on loopback with a temporary data directory.
	/// </summary>
	public sealed class TestServer : IDisposable
	{
		public readonly string DataDirectory;
		public readonly int Port;
		public readonly KvEngine Engine;
		public readonly PublicApiServer Server;
		public readonly HttpClient Client;
		private Settings settings;
		public Settings Settings => settings;

		/// <summary>
		/// Starts a server.  Maintenance is not started; tests run sweeps explicitly.
		/// </summary>
		/// <param name="configure">Optional changes to the default settings.</param>
		public TestServer(Action<Settings> configure = null)
		{
			DataDirectory = Path.Combine(Path.GetTempPath(), "KVStoreTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(DataDirectory);
			Port = GetUnusedPort();

			Settings s = new Settings();
			s.publicIpAddress = "127.0.0.1";
			s.publicHttpPort = Port;
			s.adminHttpsPort = Port == Settings.DefaultAdminPort ? Settings.DefaultAdminPort + 1 : Settings.DefaultAdminPort;
			s.buckets.Add(new BucketConfig() { name = "default" });
			s.buckets.Add(new BucketConfig() { name = "photos" });
			s.buckets.Add(new BucketConfig() { name = "disabled", enabled = false });
			configure?.Invoke(s);
			KVStoreService.ValidateSettings(s);
			settings = s;

			Engine = new KvEngine(DataDirectory, () => settings);
			Engine.Start(false);
			Server = new PublicApiServer(Engine);
			Server.UpdateBindings();

			SocketsHttpHandler handler = new SocketsHttpHandler() { UseProxy = false };
			Client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:" + Port + "/"), Timeout = TimeSpan.FromSeconds(60) };
			Client.DefaultRequestHeaders.ExpectContinue = false;
			WaitUntilListening();
		}
		private void WaitUntilListening()
		{
			for (int i = 0; i < 100; i++)
			{
				try
				{
					using (TcpClient c = new TcpClient())
					{
						c.Connect(IPAddress.Loopback, Port);
						return;
					}
				}
				catch (SocketException)
				{
					Thread.Sleep(50);
				}
			}
			throw new Exception("Test server did not start listening.");
		}
		public static int GetUnusedPort()
		{
			TcpListener l = new TcpListener(IPAddress.Loopback, 0);
			l.Start();
			int port = ((IPEndPoint)l.LocalEndpoint).Port;
			l.Stop();
			return port;
		}
		/// <summary>
		/// Applies changes to the settings the same way the service does: clone, modify, validate, activate.
		/// </summary>
		public void UpdateSettings(Action<Settings> modify)
		{
			Settings s = JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(settings));
			modify(s);
			KVStoreService.ValidateSettings(s);
			settings = s;
			Engine.ApplySettings(s);
			Server.UpdateBindings();
		}
		/// <summary>
		/// Sends a POST request to a public API endpoint and returns the status code and parsed JSON body.
		/// </summary>
		/// <param name="endpoint">e.g. "put"</param>
		/// <param name="body">Object to serialize as the JSON body, or a string to send verbatim, or null for no body.</param>
		/// <param name="clientIp">If not null, sent as the CF-Connecting-IP header.</param>
		/// <param name="contentType">Content-Type of the body.</param>
		public async Task<ApiResult> Post(string endpoint, object body, string clientIp = null, string contentType = "text/plain;charset=UTF-8")
		{
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "v1/" + endpoint);
			if (body != null)
			{
				string str = body as string ?? JsonConvert.SerializeObject(body);
				req.Content = new StringContent(str);
				req.Content.Headers.Remove("Content-Type");
				req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
			}
			if (clientIp != null)
				req.Headers.TryAddWithoutValidation("CF-Connecting-IP", clientIp);
			return await Send(req).ConfigureAwait(false);
		}
		public async Task<ApiResult> Send(HttpRequestMessage req)
		{
			using (HttpResponseMessage res = await Client.SendAsync(req).ConfigureAwait(false))
			{
				string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
				JObject json = null;
				try
				{
					json = JObject.Parse(text);
				}
				catch (JsonException) { }
				Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var h in res.Headers)
					headers[h.Key] = string.Join(", ", h.Value);
				foreach (var h in res.Content.Headers)
					headers[h.Key] = string.Join(", ", h.Value);
				return new ApiResult((int)res.StatusCode, json, text, headers);
			}
		}
		/// <summary>
		/// Returns a random strict-format key (32 lower case base32 characters).
		/// </summary>
		public static string NewKey()
		{
			return Base32.Encode(ByteUtil.GenerateRandomBytes(20));
		}
		public void Dispose()
		{
			Client.Dispose();
			Server.Stop();
			Engine.Dispose();
			try
			{
				Directory.Delete(DataDirectory, true);
			}
			catch { }
		}
	}
	public class ApiResult
	{
		public readonly int Status;
		public readonly JObject Json;
		public readonly string Text;
		public readonly Dictionary<string, string> Headers;
		public ApiResult(int status, JObject json, string text, Dictionary<string, string> headers)
		{
			Status = status;
			Json = json;
			Text = text;
			Headers = headers;
		}
		public string Error => (string)Json?["error"];
		public bool Ok => Json?["ok"]?.Value<bool>() == true;
		public override string ToString()
		{
			return Status + " " + (Text?.Length > 300 ? Text.Substring(0, 300) + "..." : Text);
		}
	}
}
