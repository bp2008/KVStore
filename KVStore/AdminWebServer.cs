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
			Settings settings = KVStoreService.MakeLocalSettingsReference();

			// HTTP Digest Authentication
			NetworkCredential[] credentials = new NetworkCredential[] { new NetworkCredential(settings.adminUser, settings.adminPass) };
			if (p.ValidateDigestAuth(AuthRealm, credentials) == null)
			{
				p.Response.Simple("401 Unauthorized");
				p.Response.Headers.Set("WWW-Authenticate", p.GetDigestAuthWWWAuthenticateHeaderValue(AuthRealm));
				return;
			}

			if (await mvcAdminConsole.ProcessRequestAsync(p, cancellationToken: cancellationToken).ConfigureAwait(false))
				return;

			if (p.Request.Page.IEquals(""))
			{
				// Placeholder until the Admin Console user interface is built.
				p.Response.FullResponseUTF8("<!DOCTYPE html><html><head><title>KVStore Admin Console</title></head><body>"
					+ "<h1>KVStore Admin Console</h1>"
					+ "<p>KVStore " + Globals.AssemblyVersion + " is running.  The Admin Console user interface has not been built yet.</p>"
					+ "</body></html>", "text/html; charset=utf-8");
				return;
			}

			p.Response.Simple("404 Not Found");
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
