using BPUtil;
using System;
using System.Collections.Generic;
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

		protected override SerializableObjectJson DeserializeFromJson(string str)
		{
			return Newtonsoft.Json.JsonConvert.DeserializeObject<Settings>(str);
		}

		protected override string SerializeToJson(object obj)
		{
			return Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
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
	}
}
