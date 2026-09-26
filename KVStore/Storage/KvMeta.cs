using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>Metadata for one stored item.  Stored in LiteDB; the value itself is a file on disk.</para>
	/// <para>There is deliberately no field for the uploader's IP address.  Client IP addresses are never written to disk.</para>
	/// </summary>
	public class KvMeta
	{
		/// <summary>
		/// "&lt;bucket&gt;:&lt;key&gt;" (the LiteDB _id).
		/// </summary>
		public string Id { get; set; }
		/// <summary>
		/// Normalized bucket name.
		/// </summary>
		public string Bucket { get; set; }
		/// <summary>
		/// Size of the value in bytes (as stored on disk).
		/// </summary>
		public long Size { get; set; }
		/// <summary>
		/// Time the item was stored, in seconds since the unix epoch.
		/// </summary>
		public long Created { get; set; }
		/// <summary>
		/// Time the item expires, in seconds since the unix epoch.
		/// </summary>
		public long Expires { get; set; }

		/// <summary>
		/// Gets the normalized key, which is the part of <see cref="Id"/> after the bucket name.
		/// </summary>
		/// <returns></returns>
		public string GetKey()
		{
			return Id.Substring(Bucket.Length + 1);
		}
	}
}
