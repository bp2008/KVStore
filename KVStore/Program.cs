using BPUtil;
using BPUtil.Forms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if !LINUX
using System.Windows.Forms;
using KVStore.ServiceUI;
#endif

namespace KVStore
{
	static class Program
	{
		/// <summary>
		/// Gets the name of the service, usable for service management.
		/// </summary>
		public static string serviceName { get; private set; }
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		static int Main()
		{
			WindowsServiceInitOptions options = new WindowsServiceInitOptions();
#if LINUX
			options.ServiceName = serviceName = "kvstore";
			options.LinuxCommandLineInterface = runCommandLineInterface;
			options.LinuxOnInstall = runLinuxOnInstallCallback;
#else
			options.ServiceName = serviceName = "KVStore";
			options.ServiceManagerButtons = new ButtonDefinition[] {
				new ButtonDefinition("Admin Console", (object sender, EventArgs e) =>
				{
					AdminConsoleInfoForm f = new AdminConsoleInfoForm();
					f.ShowDialog();
				})
			};
#endif
			AppInit.WindowsService<KVStoreService>(options);
#if LINUX
			// AppInit handled the "install" / "uninstall" arguments (if present).  The systemd sandboxing directives are maintained in a drop-in file alongside BPUtil's generated unit file.
			string[] args = Environment.GetCommandLineArgs();
			if (args.Length > 1 && args[1] == "install")
				LinuxSystemdHardening.Apply(serviceName);
			else if (args.Length > 1 && args[1] == "uninstall")
				LinuxSystemdHardening.Remove(serviceName);
#endif
			return 0;
		}

#if LINUX
		private static EConsole c = EConsole.I;
		private static void runCommandLineInterface()
		{
			c.CyanLine("Running KVStore " + Globals.AssemblyVersion + " in command-line mode.");
			c.Line();
			c.Line("Data directory:").CyanLine("\t" + Globals.WritableDirectoryBase);
			c.Line();
			WriteUsage();
			string input = Console.ReadLine();
			while (input != null && input != "exit")
			{
				c.Line();
				if (input == "admin")
				{
					printAdminInfo();
				}
				else if (input == "install")
				{
					AppInit.InstallLinuxSystemdService(serviceName, new WindowsServiceInitOptions() { LinuxOnInstall = runLinuxOnInstallCallback });
					LinuxSystemdHardening.Apply(serviceName);
				}
				else if (input == "uninstall")
				{
					AppInit.UninstallLinuxSystemdService(serviceName);
					LinuxSystemdHardening.Remove(serviceName);
				}
				else if (input == "status")
				{
					AppInit.StatusLinuxSystemdService(serviceName);
				}
				else if (input == "start")
				{
					AppInit.StartLinuxSystemdService(serviceName);
				}
				else if (input == "stop")
				{
					AppInit.StopLinuxSystemdService(serviceName);
				}
				else if (input == "restart")
				{
					AppInit.RestartLinuxSystemdService(serviceName);
				}
				else if (input == "readconfig")
				{
					AdminCommandLineInterfaceAPICall("ReadConfig");
				}
				else if (input == "loadconfig")
				{
					AdminCommandLineInterfaceAPICall("LoadConfig");
				}
				else if (input == "saveconfig")
				{
					AdminCommandLineInterfaceAPICall("SaveConfig");
				}
				else if (input == "takedown")
				{
					c.Line("Deletes one stored item, e.g. in response to an abuse report.  Items can not be listed, so the exact bucket and key are required.");
					c.Write("Bucket (leave empty for the default bucket): ");
					string bucket = Console.ReadLine()?.Trim();
					c.Write("Key: ");
					string key = Console.ReadLine()?.Trim();
					if (string.IsNullOrEmpty(key))
						c.RedLine("No key was entered.");
					else
						AdminCommandLineInterfaceAPICall("DeleteItem", new { bucket, key });
				}
				else
				{
					c.RedLine("Unrecognized command");
				}
				WriteUsage();
				input = Console.ReadLine();
			}
		}
		private static WebRequestUtility wru = new WebRequestUtility("KVStore Command Line Interface", 4000) { AcceptAnyCertificate = true };
		private static void AdminCommandLineInterfaceAPICall(string methodName, object requestBody = null)
		{
			try
			{
				AdminInfo adminInfo = new AdminInfo();
				UriBuilder builder = new UriBuilder(adminInfo.httpsUrl ?? adminInfo.httpUrl);
				builder.Path = "/CommandLineInterface/" + methodName;
				if (!string.IsNullOrWhiteSpace(adminInfo.user) && wru.BasicAuthCredentials == null)
					wru.BasicAuthCredentials = new NetworkCredential(adminInfo.user, adminInfo.pass);
				byte[] body = requestBody == null ? new byte[0] : ByteUtil.Utf8NoBOM.GetBytes(JsonConvert.SerializeObject(requestBody));
				BpWebResponse response = wru.POST(builder.Uri.ToString(), body, "application/json", new string[] { "X-KVStore-CSRF-Protection", "1" });
				if (response.StatusCode == 0)
					c.RedLine("Failed to get a response from the service. Is it running? Tried " + builder.Uri.ToString() + " and got " + (response.ex == null ? "No Response" : response.ex.ToHierarchicalString()));
				else if (response.StatusCode != 200)
					c.RedLine("HTTP " + response.StatusCode + " response from admin console: " + response.str);
				else
				{
					dynamic responseData = JsonConvert.DeserializeObject(response.str);
					if (responseData == null)
						c.RedLine("HTTP 200 response from admin console could not be deserialized from JSON: " + response.str);
					else if (responseData.success == true)
						c.Line((string)responseData.message);
					else if (responseData.success == false)
						c.RedLine((string)responseData.error);
					else
						c.YellowLine(response.str);
				}
			}
			catch (Exception ex)
			{
				c.RedLine(ex.ToHierarchicalString());
			}
		}
		private static void WriteUsage()
		{
			c.WriteLine();
			c.WriteLine("Commands:");
			ConsoleAppHelper.MaxCommandSize = 21;
			ConsoleAppHelper.WriteUsageCommand("admin", "Display admin console login link and credentials.");
			ConsoleAppHelper.WriteUsageCommand("install", "Install as service using systemd.");
			ConsoleAppHelper.WriteUsageCommand("uninstall", "Uninstall as service using systemd.");
			ConsoleAppHelper.WriteUsageCommand("status", "Display the service status.");
			ConsoleAppHelper.WriteUsageCommand("start", "Start the service.");
			ConsoleAppHelper.WriteUsageCommand("stop", "Stop the service.");
			ConsoleAppHelper.WriteUsageCommand("restart", "Restart the service.");
			ConsoleAppHelper.WriteUsageCommand("readconfig", "Read current configuration from the running service and display it here.");
			ConsoleAppHelper.WriteUsageCommand("loadconfig", "Instruct the running service to validate and reload the Settings.json file.");
			ConsoleAppHelper.WriteUsageCommand("saveconfig", "Instruct the running service to save its current settings to the Settings.json file.");
			ConsoleAppHelper.WriteUsageCommand("takedown", "Delete one stored item by bucket and key (for abuse reports).");
			ConsoleAppHelper.WriteUsageCommand("exit", "Close this command line interface.");
			c.WriteLine();
		}

		private static void runLinuxOnInstallCallback()
		{
			printAdminInfo();
		}
		private static void printAdminInfo()
		{
			AdminInfo adminInfo = new AdminInfo();
			c.YellowLine("------Credentials------");
			c.Yellow("User: ").WriteLine(adminInfo.user);
			c.Yellow("Pass: ").WriteLine(adminInfo.pass);
			c.YellowLine("---------URLs----------");
			if (adminInfo.httpUrl != null)
				c.Cyan(adminInfo.httpUrl).YellowLine(" (" + adminInfo.adminIp + ")");
			if (adminInfo.httpsUrl != null)
				c.Cyan(adminInfo.httpsUrl).YellowLine(" (" + adminInfo.adminIp + ")");
			c.YellowLine("-----------------------");
		}
#endif
	}
}
