using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Serves the Admin Console's static files, which are embedded in the assembly (see the "AdminUI" folder).
	/// </summary>
	public static class AdminUiFiles
	{
		private const string resourcePrefix = "AdminUI.";
		private static readonly Lazy<Dictionary<string, byte[]>> files = new Lazy<Dictionary<string, byte[]>>(Load);
		private static Dictionary<string, byte[]> Load()
		{
			Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			Assembly assembly = typeof(AdminUiFiles).Assembly;
			foreach (string name in assembly.GetManifestResourceNames())
			{
				if (!name.StartsWith(resourcePrefix, StringComparison.Ordinal))
					continue;
				using (Stream s = assembly.GetManifestResourceStream(name))
				using (MemoryStream ms = new MemoryStream())
				{
					s.CopyTo(ms);
					result[name.Substring(resourcePrefix.Length)] = ms.ToArray();
				}
			}
			return result;
		}
		/// <summary>
		/// Gets the named file.  Returns false if there is no such file.
		/// </summary>
		/// <param name="fileName">File name, e.g. "index.html".</param>
		/// <param name="body">(Output) File contents.</param>
		/// <param name="contentType">(Output) Content-Type header value.</param>
		/// <returns></returns>
		public static bool TryGet(string fileName, out byte[] body, out string contentType)
		{
			contentType = null;
			if (!files.Value.TryGetValue(fileName, out body))
				return false;
			switch (Path.GetExtension(fileName).ToLowerInvariant())
			{
				case ".html": contentType = "text/html; charset=utf-8"; break;
				case ".js": contentType = "text/javascript; charset=utf-8"; break;
				case ".css": contentType = "text/css; charset=utf-8"; break;
				case ".svg": contentType = "image/svg+xml"; break;
				default: contentType = "application/octet-stream"; break;
			}
			return true;
		}
	}
}
