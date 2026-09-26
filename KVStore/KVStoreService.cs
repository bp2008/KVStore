using BPUtil;
using BPUtil.SimpleHttp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// Singleton service class for KVStore.
	/// </summary>
	public partial class KVStoreService
#if !LINUX
		: ServiceBase
#endif
	{
		/// <summary>
		/// Reference to the constructed KVStoreService instance. Null if none has been constructed yet.
		/// </summary>
		public static KVStoreService service;
		/// <summary>
		/// The Admin Console web server.
		/// </summary>
		private static AdminWebServer adminWebServer;
		/// <summary>
		/// The public API web server.
		/// </summary>
		private static PublicApiServer publicApiServer;
		/// <summary>
		/// The engine (storage, rate limiters, counters, maintenance).  Null until the service has started.
		/// </summary>
		public static KvEngine Engine { get; private set; }
		/// <summary>
		/// Gets the directory where the database and stored values are kept.  It is inside <see cref="Globals.WritableDirectoryBase"/>, which is outside the published binary directory.
		/// </summary>
		public static string DataDirectory => Path.Combine(Globals.WritableDirectoryBase, "data");
		/// <summary>
		/// This is set = true when the service's OnStop method is called.
		/// </summary>
		public static bool abort { get; private set; } = false;
		public KVStoreService()
		{
			if (service != null)
				throw new Exception("Unable to create KVStoreService because one was already created.");

			InitializeSettings();

#if !LINUX
			InitializeComponent();
#endif

			service = this;
		}

		/// <summary>
		/// Loads the settings file, saves it if it does not exist, and then validates the settings and ensures the admin console is available.
		/// </summary>
		public static void InitializeSettings()
		{
			Settings s = new Settings();
			s.Load();
			string settingsOriginal = JsonConvert.SerializeObject(s);
			ValidateSettings(s);
			string settingsAfterValidation = JsonConvert.SerializeObject(s);

			if (settingsOriginal != settingsAfterValidation)
				TaskHelper.RunAsyncCodeSafely(() => KVStoreService.SaveNewSettings(s));
			else
			{
				staticSettings = s;
				staticSettings.SaveIfNoExist();
			}

			ActivateSettingsChanges(s);
		}
		/// <summary>
		/// Logs the Exception to file, with any IP addresses removed.
		/// </summary>
		/// <param name="ex">Exception to log.</param>
		public static void ReportError(Exception ex)
		{
			ReportError(ex, null);
		}

		/// <summary>
		/// Logs the Exception to file, with any IP addresses removed.
		/// </summary>
		/// <param name="ex">Exception to log.</param>
		/// <param name="additionalInformation">Optional additional information to log with the exception.</param>
		public static void ReportError(Exception ex, string additionalInformation)
		{
			StringBuilder sb = new StringBuilder();
			if (!string.IsNullOrEmpty(additionalInformation))
				sb.AppendLine(additionalInformation);
			if (ex != null)
				sb.Append(ex.ToHierarchicalString());
			ReportError(sb.ToString());
		}
		/// <summary>
		/// Logs the message to file, with any IP addresses removed.  Client IP addresses must never be written to disk.
		/// </summary>
		/// <param name="message">Message to log.</param>
		public static void ReportError(string message)
		{
			Logger.Debug(IpScrubber.Scrub(message));
		}

#if LINUX
		protected void OnStart(string[] args)
		{
			DoStart(args);
		}

		protected void OnStop()
		{
			DoStop();
		}
#else
		protected override void OnStart(string[] args)
		{
			DoStart(args);
		}

		protected override void OnStop()
		{
			DoStop();
		}
#endif
		protected void DoStart(string[] args)
		{
			Logger.Info(Globals.AssemblyName + " " + Globals.AssemblyVersion + " Starting Up");

			// BPUtil's default HTTP server logger writes client IP addresses (in request logs and in error messages).  Replace it before any server is constructed, because each server's constructor would otherwise register the default logger.
			HttpServerBase.EnableLoggingByDefault = false;
			SimpleHttpLogger.RegisterLogger(new ScrubbingHttpLogger(), false);

			KvEngine engine = new KvEngine(DataDirectory, MakeLocalSettingsReference);
			engine.Start();
			Engine = engine;

			publicApiServer = new PublicApiServer(engine);
			adminWebServer = new AdminWebServer();
			ActivateSettingsChanges(MakeLocalSettingsReference());
		}

		protected void DoStop()
		{
			Logger.Info(Globals.AssemblyName + " " + Globals.AssemblyVersion + " Shutting Down");
			abort = true;
			publicApiServer?.Stop();
			adminWebServer?.Stop();
			Engine?.Dispose();
		}

		/// <summary>
		/// Updates the admin web server bindings according to the current configuration.  It is safe to call this even if bindings have not changed.
		/// </summary>
		public static void UpdateAdminWebServerBindings()
		{
			adminWebServer?.UpdateBindings();
		}
		/// <summary>
		/// Updates the public API web server bindings according to the current configuration.  It is safe to call this even if bindings have not changed.
		/// </summary>
		public static void UpdatePublicApiServerBindings()
		{
			publicApiServer?.UpdateBindings();
		}
		#region Settings
		/// <summary>
		/// <para>Static settings object. To retain maximum performance and exception safety without locks, some usage constraints are necessary:</para>
		/// <para>* To read the settings, call MakeLocalSettingsReference and store the returned value in a local variable.  Treat the fields/properties of the settings object as read-only.</para>
		/// <para>* To write/change anything in settings, make a local COPY of the settings object via CloneSettingsObjectSlow(), edit the copy, then pass it to SaveNewSettings().</para>
		/// </summary>
		private static Settings staticSettings;

		/// <summary>
		/// Returns a reference to the settings object which must be treated as read-only.  Store the returned value in a local variable and use it from there, because calling this method is not guaranteed to return the same object each time.  Failure to treat the returned object as read-only will yield race conditions and errors in other threads.
		/// </summary>
		/// <returns></returns>
		public static Settings MakeLocalSettingsReference()
		{
			return staticSettings;
		}
		/// <summary>
		/// Returns a detached copy of the settings.  You can modify the returned object and send it to SaveNewSettings().
		/// </summary>
		/// <returns></returns>
		public static Settings CloneSettingsObjectSlow()
		{
			return JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(staticSettings));
		}
		private static object settingsSaveLock = new object();
		/// <summary>
		/// Replaces the internal settings object with this one and saves the settings to disk in a thread-safe manner.  You should not modify the settings object again after calling this; instead, make a new clone of the settings object if you need to make more changes.
		/// </summary>
		/// <param name="newSettings">A clone of the settings object.  The clone contains changes that you want to save.</param>
		/// <param name="cancellationToken">Cancellation Token</param>
		public static async Task SaveNewSettings(Settings newSettings, CancellationToken cancellationToken = default)
		{
			ValidateSettings(newSettings);

			await TaskHelper.RunBlockingCodeSafely(() =>
			{
				lock (settingsSaveLock)
				{
					staticSettings = newSettings;
					newSettings.Save();
				}
			}, cancellationToken).ConfigureAwait(false);

			ActivateSettingsChanges(newSettings);
		}
		/// <summary>
		/// <para>Applies settings from the given settings object to this service:</para>
		/// <para>* updates admin web server bindings</para>
		/// <para>* updates public API web server bindings</para>
		/// <para>* replaces the rate limiters if their settings changed</para>
		/// <para>All other settings are read by each request, so they take effect immediately.</para>
		/// </summary>
		/// <param name="s">Settings object to apply settings from.</param>
		private static void ActivateSettingsChanges(Settings s)
		{
			UpdateAdminWebServerBindings();
			UpdatePublicApiServerBindings();
			Engine?.ApplySettings(s);
		}
		/// <summary>
		/// Validate the settings file and repair simple problems, including creating/repairing admin console access (a random admin password is generated if none is set).  Throw an exception if anything is invalid that can't be cleanly repaired automatically.  Because this can modify the settings, this should never be passed the static settings instance, and should only be called just prior to saving the settings.  This method is automatically called by <see cref="SaveNewSettings"/>.
		/// </summary>
		/// <param name="s">Settings instance containing settings that need to be validated.</param>
		/// <exception cref="Exception">If validation fails.</exception>
		public static void ValidateSettings(Settings s)
		{
			if (s == staticSettings)
				throw new Exception("Application error: Refusing to run ValidateSettings on the static settings instance due to causing race conditions.");

			// Validate Admin Console
			if (!string.IsNullOrWhiteSpace(s.adminIpAddress))
			{
				s.adminIpAddress = s.adminIpAddress.Trim();
				if (!IPAddress.TryParse(s.adminIpAddress, out IPAddress ignored))
					throw new Exception("adminIpAddress \"" + s.adminIpAddress + "\" is not a valid IP address.");
			}

			if (!s.adminHttpPortValid())
				s.adminHttpPort = -1;
			if (!s.adminHttpsPortValid())
				s.adminHttpsPort = -1;
			if (!s.adminHttpPortValid() && !s.adminHttpsPortValid())
				s.adminHttpsPort = Settings.DefaultAdminPort;

			if (string.IsNullOrWhiteSpace(s.adminUser))
				s.adminUser = "kvadmin";
			s.adminUser = s.adminUser.Trim();

			if (string.IsNullOrEmpty(s.adminPass))
				s.adminPass = StringUtil.GetRandomAlphaNumericString(16);

			// Validate Public API
			if (!string.IsNullOrWhiteSpace(s.publicIpAddress))
			{
				s.publicIpAddress = s.publicIpAddress.Trim();
				if (!IPAddress.TryParse(s.publicIpAddress, out IPAddress ignored))
					throw new Exception("publicIpAddress \"" + s.publicIpAddress + "\" is not a valid IP address.");
			}
			if (!s.publicHttpPortValid())
				s.publicHttpPort = -1;
			if (s.publicHttpPortValid() && (s.publicHttpPort == s.adminHttpPort || s.publicHttpPort == s.adminHttpsPort))
				throw new Exception("The public API port (" + s.publicHttpPort + ") must be different from the Admin Console ports.");
			s.abuseContact = s.abuseContact?.Trim() ?? "";
			s.operatorName = s.operatorName?.Trim() ?? "";

			// Validate Buckets
			if (s.bucketDefaults == null)
				s.bucketDefaults = new BucketConfig();
			s.bucketDefaults.RepairLimits();
			if (s.buckets == null)
				s.buckets = new List<BucketConfig>();
			s.buckets.RemoveAll(b => b == null);
			HashSet<string> bucketNames = new HashSet<string>();
			foreach (BucketConfig b in s.buckets)
			{
				if (!KvNames.TryNormalizeBucketName(b.name, out string normalized))
					throw new Exception("Bucket name \"" + b.name + "\" is invalid.  Bucket names must be 1-" + KvNames.BucketNameMaxLength + " characters from the base32 alphabet (a-z, 2-7).");
				b.name = normalized;
				if (!bucketNames.Add(b.name))
					throw new Exception("Bucket name \"" + b.name + "\" is used by more than one bucket.  Bucket names are not case-sensitive.");
				b.RepairLimits();
			}
			if (KvNames.TryNormalizeBucketName(s.defaultBucketName, out string defaultBucketName))
				s.defaultBucketName = defaultBucketName;
			else
				s.defaultBucketName = "default";
			BucketConfig defaultBucket = s.buckets.FirstOrDefault(b => b.name == s.defaultBucketName);
			if (defaultBucket == null)
			{
				defaultBucket = s.bucketDefaults.Clone();
				defaultBucket.name = s.defaultBucketName;
				s.buckets.Insert(0, defaultBucket);
			}
			defaultBucket.enabled = true; // The default bucket can not be disabled.
			s.InvalidateBucketLookup();

			// Validate Rate Limits and Maintenance
			if (s.rateLimits == null)
				s.rateLimits = new RateLimitSettings();
			s.rateLimits.Repair();
			if (s.maintenance == null)
				s.maintenance = new MaintenanceSettings();
			s.maintenance.Repair();
		}
		#endregion
	}
}
