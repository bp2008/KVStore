using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// A set of static files which are embedded in the assembly.
	/// </summary>
	public class EmbeddedFiles
	{
		/// <summary>
		/// The Admin Console's files (see the "AdminUI" folder).
		/// </summary>
		public static readonly EmbeddedFiles AdminUI = new EmbeddedFiles("AdminUI.");
		/// <summary>
		/// The public web pages' files (see the "PublicSite" folder).
		/// </summary>
		public static readonly EmbeddedFiles PublicSite = new EmbeddedFiles("PublicSite.");

		private readonly string resourcePrefix;
		private readonly Lazy<Dictionary<string, byte[]>> files;
		private EmbeddedFiles(string resourcePrefix)
		{
			this.resourcePrefix = resourcePrefix;
			files = new Lazy<Dictionary<string, byte[]>>(Load);
		}
		private Dictionary<string, byte[]> Load()
		{
			Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			Assembly assembly = typeof(EmbeddedFiles).Assembly;
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
		public bool TryGet(string fileName, out byte[] body, out string contentType)
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
		/// <summary>
		/// Gets the named file as UTF-8 text.  Throws if there is no such file.
		/// </summary>
		/// <param name="fileName">File name, e.g. "index.html".</param>
		/// <returns></returns>
		public string GetText(string fileName)
		{
			if (!files.Value.TryGetValue(fileName, out byte[] body))
				throw new FileNotFoundException("Embedded file \"" + resourcePrefix + fileName + "\" was not found.");
			return Encoding.UTF8.GetString(body);
		}
	}
}
