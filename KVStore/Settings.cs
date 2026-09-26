using BPUtil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// KVStore's primary settings file.
	/// </summary>
	public class Settings : SerializableObjectJson
	{
		/// <summary>
		/// Default TCP port for the Admin Console.
		/// </summary>
		public const int DefaultAdminPort = 8081;
		/// <summary>
		/// <para>IP address which the Admin Console listens on.  If null or empty, the Admin Console listens on all interfaces.</para>
		/// <para>The Admin Console must never be exposed to the public internet.  Bind it to loopback (the default, for use via an SSH tunnel) or to a VPN interface address.</para>
		/// </summary>
		public string adminIpAddress = "127.0.0.1";
		/// <summary>
		/// [1-65535] TCP port for the Admin Console's HTTP listener, or -1 to disable HTTP.  Disabled by default because HTTP Digest authentication is not a substitute for TLS.  If this is the same as <see cref="adminHttpsPort"/>, one listener accepts both protocols.
		/// </summary>
		public int adminHttpPort = -1;
		/// <summary>
		/// [1-65535] TCP port for the Admin Console's HTTPS listener, or -1 to disable HTTPS.  A self-signed certificate is generated automatically.
		/// </summary>
		public int adminHttpsPort = DefaultAdminPort;
		/// <summary>
		/// User name for the Admin Console (HTTP Digest authentication).
		/// </summary>
		public string adminUser = "kvadmin";
		/// <summary>
		/// Password for the Admin Console (HTTP Digest authentication).  If null or empty, a random password is generated at startup.  HTTP Digest authentication requires the server to know the plain text password, so it can not be stored as a one-way hash.
		/// </summary>
		public string adminPass = null;

		/// <summary>
		/// Default TCP port for the public API.
		/// </summary>
		public const int DefaultPublicPort = 8080;
		/// <summary>
		/// <para>IP address which the public API listens on.  If null or empty, the public API listens on all interfaces.</para>
		/// <para>The default is loopback, because the public API is meant to be exposed to the internet only through a Cloudflare Tunnel (cloudflared) running on the same machine.</para>
		/// </summary>
		public string publicIpAddress = "127.0.0.1";
		/// <summary>
		/// [1-65535] TCP port for the public API's HTTP listener, or -1 to disable the public API.  TLS is terminated by Cloudflare, so the public API does not offer HTTPS itself.
		/// </summary>
		public int publicHttpPort = DefaultPublicPort;
		/// <summary>
		/// An email address or URL where abuse reports and takedown requests can be sent.  Shown on the public landing page.
		/// </summary>
		public string abuseContact = "";
		/// <summary>
		/// Name of the operator of this service.  Shown on the public landing page and in the Terms of Service.
		/// </summary>
		public string operatorName = "";

		/// <summary>
		/// Name of the default bucket, which is used when a request does not name a bucket.  The default bucket always exists and can not be deleted or disabled.
		/// </summary>
		public string defaultBucketName = "default";
		/// <summary>
		/// The template applied to newly created buckets.  Changing these values does not alter existing buckets.
		/// </summary>
		public BucketConfig bucketDefaults = new BucketConfig();
		/// <summary>
		/// All configured buckets, including the default bucket.  Buckets are created only by the administrator; the public API can never create one.
		/// </summary>
		public List<BucketConfig> buckets = new List<BucketConfig>();
		/// <summary>
		/// <para>If false (default), keys must be exactly 32 base32 characters (<c>^[a-z2-7]{32}$</c>, case-insensitive, normalized to lower case).</para>
		/// <para>If true, keys matching <c>^[A-Za-z0-9_.-]{20,128}$</c> are also accepted.  Such keys are case-sensitive, except that a key which satisfies the strict format is always normalized to lower case.</para>
		/// </summary>
		public bool permissiveKeys = false;
		/// <summary>
		/// Per-client rate limits for the public API.
		/// </summary>
		public RateLimitSettings rateLimits = new RateLimitSettings();
		/// <summary>
		/// Settings for the background maintenance tasks.
		/// </summary>
		public MaintenanceSettings maintenance = new MaintenanceSettings();

		/// <summary>
		/// A lookup table of <see cref="buckets"/> keyed on bucket name.  Built lazily.  Settings objects are not modified after they are activated, so the table remains valid for the lifetime of this instance.
		/// </summary>
		private Dictionary<string, BucketConfig> bucketLookup = null;
		/// <summary>
		/// Returns the bucket with the given (normalized) name, or null if no such bucket is configured.
		/// </summary>
		/// <param name="normalizedName">Bucket name that has already been normalized to lower case.</param>
		/// <returns></returns>
		public BucketConfig GetBucket(string normalizedName)
		{
			Dictionary<string, BucketConfig> lookup = bucketLookup;
			if (lookup == null)
			{
				lookup = new Dictionary<string, BucketConfig>();
				foreach (BucketConfig b in buckets)
					if (b?.name != null)
						lookup[b.name] = b;
				bucketLookup = lookup;
			}
			lookup.TryGetValue(normalizedName, out BucketConfig bucket);
			return bucket;
		}
		/// <summary>
		/// Returns the default bucket.
		/// </summary>
		/// <returns></returns>
		public BucketConfig GetDefaultBucket()
		{
			return GetBucket(defaultBucketName);
		}
		/// <summary>
		/// Discards the lookup table used by <see cref="GetBucket"/>.  Call after modifying <see cref="buckets"/> on a settings object that has not been activated yet.
		/// </summary>
		public void InvalidateBucketLookup()
		{
			bucketLookup = null;
		}

		protected override SerializableObjectJson DeserializeFromJson(string str)
		{
			return Newtonsoft.Json.JsonConvert.DeserializeObject<Settings>(str);
		}

		protected override string SerializeToJson(object obj)
		{
			return Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
		}

		/// <summary>
		/// Saves this instance to file.  Returns true if successful.  On Linux, the file is kept at permission mode 0600 (owner read/write only) because it contains the Admin Console password.
		/// </summary>
		/// <param name="filePath">Optional file path. If null, the default file path is used.</param>
		/// <returns></returns>
		public override bool Save(string filePath = null)
		{
			if (filePath == null)
				filePath = GetDefaultFilePath();
			RestrictFilePermissions(filePath, true);
			return base.Save(filePath);
		}

		/// <summary>
		/// Loads this instance from file.  Returns true if successful.  May throw an exception if the file format is invalid.  On Linux, the file is first restricted to permission mode 0600 (owner read/write only) because it contains the Admin Console password.
		/// </summary>
		/// <param name="filePath">Optional file path. If null, the default file path is used.</param>
		/// <returns></returns>
		public override bool Load(string filePath = null)
		{
			if (filePath == null)
				filePath = GetDefaultFilePath();
			RestrictFilePermissions(filePath, false);
			return base.Load(filePath);
		}

		/// <summary>
		/// On Linux, restricts the file to permission mode 0600 (owner read/write only) if it has any broader permissions.  If the file does not exist and <paramref name="createIfMissing"/> is true, an empty file is created with mode 0600 so that the settings are never written to a file which other users can read, even briefly.  Does nothing on Windows.
		/// </summary>
		/// <param name="filePath">Path to the settings file.</param>
		/// <param name="createIfMissing">If true, the file is created if it does not exist.</param>
		private static void RestrictFilePermissions(string filePath, bool createIfMissing)
		{
			if (OperatingSystem.IsWindows())
				return;
			const UnixFileMode ownerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			try
			{
				if (File.Exists(filePath))
				{
					if ((File.GetUnixFileMode(filePath) & ~ownerReadWrite) != 0)
						File.SetUnixFileMode(filePath, ownerReadWrite);
				}
				else if (createIfMissing)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));
					using (new FileStream(filePath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ownerReadWrite })) { }
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "Unable to restrict permissions of \"" + filePath + "\" to 0600.");
			}
		}

		/// <summary>
		/// Returns true if the admin http port is between 1 and 65535.
		/// </summary>
		/// <returns></returns>
		public bool adminHttpPortValid()
		{
			return adminHttpPort >= 1 && adminHttpPort <= 65535;
		}
		/// <summary>
		/// Returns true if the admin https port is between 1 and 65535.
		/// </summary>
		/// <returns></returns>
		public bool adminHttpsPortValid()
		{
			return adminHttpsPort >= 1 && adminHttpsPort <= 65535;
		}
		/// <summary>
		/// Returns true if the public http port is between 1 and 65535.
		/// </summary>
		/// <returns></returns>
		public bool publicHttpPortValid()
		{
			return publicHttpPort >= 1 && publicHttpPort <= 65535;
		}
	}
}
