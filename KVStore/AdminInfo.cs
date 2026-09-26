using BPUtil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// Gets the credentials and login links for the administration console.
	/// </summary>
	public class AdminInfo
	{
		/// <summary>
		/// Username for the admin console user.
		/// </summary>
		public string user;
		/// <summary>
		/// Password for the admin console user.
		/// </summary>
		public string pass;
		/// <summary>
		/// HTTP URL of the admin console, or null.
		/// </summary>
		public string httpUrl;
		/// <summary>
		/// HTTPS URL of the admin console, or null.
		/// </summary>
		public string httpsUrl;
		/// <summary>
		/// The IP address of the admin console (for labeling purposes; this might be a descriptive string instead of an IP address).
		/// </summary>
		public string adminIp;
		/// <summary>
		/// Gets the credentials and login links for the administration console.
		/// </summary>
		public AdminInfo()
		{
			Settings s = KVStoreService.MakeLocalSettingsReference();

			adminIp = string.IsNullOrEmpty(s.adminIpAddress) ? "Any IP" : s.adminIpAddress;

			// If the admin console is bound to a specific address, the URLs must use that address because "localhost" may not reach it.
			string adminHost = "localhost";
			if (IPAddress.TryParse(s.adminIpAddress, out IPAddress ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
				adminHost = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();

			if (s.adminHttpPortValid())
				httpUrl = "http://" + adminHost + (s.adminHttpPort == 80 ? "" : (":" + s.adminHttpPort));

			if (s.adminHttpsPortValid())
				httpsUrl = "https://" + adminHost + (s.adminHttpsPort == 443 ? "" : (":" + s.adminHttpsPort));

			user = s.adminUser;
			pass = s.adminPass;
		}
	}
}
