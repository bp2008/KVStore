using BPUtil;
using BPUtil.MVC;
using BPUtil.SimpleHttp;
using KVStore.Controllers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// Web server for the Admin Console.  This listens on its own port, separately from the public API, and must never be exposed to the public internet.  Every request requires HTTP Digest authentication.
	/// </summary>
	public class AdminWebServer : HttpServerAsync
	{
		/// <summary>
		/// The realm used for HTTP Digest authentication.
		/// </summary>
		public const string AuthRealm = "KVStore Admin Console";
		MVCMain mvcAdminConsole;

		public AdminWebServer() : base(CreateCertificateSelector())
		{
			MvcJson.DeserializeObject = JsonConvert.DeserializeObject;
			MvcJson.SerializeObject = JsonConvert.SerializeObject;
			mvcAdminConsole = new MVCMain(Assembly.GetExecutingAssembly(), typeof(AdminConsoleControllerBase).Namespace, MvcErrorHandler);
		}

		private static void MvcErrorHandler(RequestContext Context, Exception ex)
		{
			if (!HttpProcessor.IsOrdinaryDisconnectException(ex))
				KVStoreService.ReportError(ex, "AdminConsole: " + Context.OriginalRequestPath);
		}

		private static ICertificateSelector CreateCertificateSelector()
		{
			// Creates the self-signed certificate file if necessary, then always loads the certificate from the file.  SelfSignedCertificateSelector would instead serve a newly generated certificate from memory, which fails the TLS handshake on Windows until the process restarts.
			CertificatePfxInfo cpi = SelfSignedCertificateSelector.GetCertificatePfxInfoForReloadingCertificateSelector(Path.Combine(Globals.WritableDirectoryBase, "AdminConsole-SslCert.pfx"));
			return new ReloadingCertificateSelector(() => cpi);
		}

		public override async Task handleRequest(HttpProcessor p, string method, CancellationToken cancellationToken = default)
		{
			await HandleRequestInternal(p, method, cancellationToken).ConfigureAwait(false);
			if (!p.Response.ResponseHeaderWritten)
				AddSecurityHeaders(p);
		}
		private async Task HandleRequestInternal(HttpProcessor p, string method, CancellationToken cancellationToken)
		{
			Settings settings = KVStoreService.MakeLocalSettingsReference();
			KvEngine engine = KVStoreService.Engine;
			if (engine == null)
			{
				p.Response.Simple("503 Service Unavailable", "The service is starting.");
				return;
			}

			// Failed authentication attempts are rate limited per client address.  The address is used only as an in-memory key and is never logged.
			string clientKey = ClientIp.GetRateLimitKey(p.RemoteIPAddress);
			int lockoutSeconds = engine.AdminAuthLimiter.GetLockoutSeconds(clientKey);
			if (lockoutSeconds > 0)
			{
				p.Response.Simple("429 Too Many Requests", "Too many failed login attempts.  Try again in " + lockoutSeconds + " seconds.");
				p.Response.Headers.Set("Retry-After", lockoutSeconds.ToString());
				return;
			}

			// HTTP Digest Authentication
			NetworkCredential[] credentials = new NetworkCredential[] { new NetworkCredential(settings.adminUser, settings.adminPass) };
			if (p.ValidateDigestAuth(AuthRealm, credentials) == null)
			{
				if (!string.IsNullOrEmpty(p.Request.Headers.Get("Authorization")))
				{
					engine.AdminAuthLimiter.RecordFailure(clientKey);
					engine.Stats.Add(OpCounter.AdminAuthFailures);
				}
				p.Response.Simple("401 Unauthorized");
				p.Response.Headers.Set("WWW-Authenticate", p.GetDigestAuthWWWAuthenticateHeaderValue(AuthRealm));
				return;
			}
			engine.AdminAuthLimiter.RecordSuccess(clientKey);

			if (await mvcAdminConsole.ProcessRequestAsync(p, cancellationToken: cancellationToken).ConfigureAwait(false))
				return;

			if (method == HttpMethods.GET || method == HttpMethods.HEAD)
			{
				string page = p.Request.Page;
				if (page == "")
					page = "index.html";
				if (AdminUiFiles.TryGet(page, out byte[] body, out string contentType))
				{
					p.Response.FullResponseBytes(body, contentType);
					return;
				}
			}

			p.Response.Simple("404 Not Found");
		}
		/// <summary>
		/// Adds headers that restrict what the browser allows the Admin Console to do.
		/// </summary>
		private static void AddSecurityHeaders(HttpProcessor p)
		{
			HttpHeaderCollection h = p.Response.Headers;
			if (h.Get("Cache-Control") == null)
				h.Set("Cache-Control", "no-store");
			h.Set("X-Content-Type-Options", "nosniff");
			h.Set("X-Frame-Options", "DENY");
			h.Set("Referrer-Policy", "no-referrer");
			h.Set("X-Robots-Tag", "noindex, nofollow");
			h.Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
		}

		/// <inheritdoc/>
		protected override void stopServer()
		{
		}

		/// <summary>
		/// If this method returns true, socket bind events will be logged normally.  If false, they will use the LogVerbose call.
		/// </summary>
		/// <returns></returns>
		public override bool shouldLogSocketBind()
		{
			return true;
		}

		/// <summary>
		/// Returns false.  Request logs contain client IP addresses, which KVStore never writes to disk.
		/// </summary>
		/// <returns></returns>
		public override bool shouldLogRequestsToFile()
		{
			return false;
		}

		private object updateBindingsLock = new object();
		/// <summary>
		/// Reconfigures the web server to listen on the Admin Console endpoints currently in the settings object.
		/// </summary>
		internal void UpdateBindings()
		{
			lock (updateBindingsLock)
			{
				Settings settings = KVStoreService.MakeLocalSettingsReference();

				// These collections will contain all IP endpoints that are configured to provide HTTP or HTTPS.
				HashSet<IPEndPoint> httpBindings = new HashSet<IPEndPoint>();
				HashSet<IPEndPoint> httpsBindings = new HashSet<IPEndPoint>();
				AddBinding(httpBindings, settings.adminHttpPort, settings.adminIpAddress);
				AddBinding(httpsBindings, settings.adminHttpsPort, settings.adminIpAddress);

				// Convert the endpoint HashSets to a list of Bindings.
				List<Binding> bindings = new List<Binding>();
				foreach (IPEndPoint ipep in httpBindings)
				{
					if (httpsBindings.Contains(ipep))
						bindings.Add(new Binding(AllowedConnectionTypes.httpAndHttps, ipep));
					else
						bindings.Add(new Binding(AllowedConnectionTypes.http, ipep));
				}
				foreach (IPEndPoint ipep in httpsBindings)
				{
					if (!httpBindings.Contains(ipep))
						bindings.Add(new Binding(AllowedConnectionTypes.https, ipep));
				}
				this.SetBindings(bindings.ToArray());
			}
		}

		private void AddBinding(HashSet<IPEndPoint> bindings, int port, string ipAddress)
		{
			if (port < 1 || port > 65535)
				return;
			if (IPAddress.TryParse(ipAddress, out IPAddress ip))
				bindings.Add(new IPEndPoint(ip, port));
			else
			{
				bindings.Add(new IPEndPoint(IPAddress.Any, port));
				bindings.Add(new IPEndPoint(IPAddress.IPv6Any, port));
			}
		}
	}
}
