using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Validation and normalization of bucket names and keys.
	/// </summary>
	public static class KvNames
	{
		/// <summary>
		/// Length of a key in strict mode (32 base32 characters = 160 bits).
		/// </summary>
		public const int StrictKeyLength = 32;
		/// <summary>
		/// Minimum length of a key in permissive mode.
		/// </summary>
		public const int PermissiveKeyMinLength = 20;
		/// <summary>
		/// Maximum length of a key in permissive mode.
		/// </summary>
		public const int PermissiveKeyMaxLength = 128;
		/// <summary>
		/// Maximum length of a bucket name.
		/// </summary>
		public const int BucketNameMaxLength = 32;
		/// <summary>
		/// Prefix of the blob file name for keys that do not satisfy the strict key format.  The underscore is not in the base32 alphabet, so these file names can not collide with strict keys.
		/// </summary>
		private const string permissiveFileNamePrefix = "p_";

		/// <summary>
		/// Validates a bucket name (1-32 base32 characters, case-insensitive) and returns it normalized to lower case.  Returns false if the name is invalid.
		/// </summary>
		/// <param name="input">Bucket name provided by a client or administrator.</param>
		/// <param name="normalized">(Output) The normalized bucket name, or null.</param>
		/// <returns></returns>
		public static bool TryNormalizeBucketName(string input, out string normalized)
		{
			normalized = null;
			if (!Base32.IsBase32String(input, 1, BucketNameMaxLength))
				return false;
			normalized = input.ToLowerInvariant();
			return true;
		}
		/// <summary>
		/// <para>Validates a key and returns it normalized.  Returns false if the key is invalid.</para>
		/// <para>A key of 32 base32 characters (<c>^[a-z2-7]{32}$</c>, case-insensitive) is always accepted and normalized to lower case.</para>
		/// <para>If <paramref name="permissive"/> is true, a key matching <c>^[A-Za-z0-9_.-]{20,128}$</c> is also accepted, and is not case-normalized.</para>
		/// </summary>
		/// <param name="input">Key provided by a client.</param>
		/// <param name="permissive">True if permissive key format is enabled.</param>
		/// <param name="normalized">(Output) The normalized key, or null.</param>
		/// <returns></returns>
		public static bool TryNormalizeKey(string input, bool permissive, out string normalized)
		{
			normalized = null;
			if (input == null)
				return false;
			if (Base32.IsBase32String(input, StrictKeyLength, StrictKeyLength))
			{
				normalized = input.ToLowerInvariant();
				return true;
			}
			if (!permissive || input.Length < PermissiveKeyMinLength || input.Length > PermissiveKeyMaxLength)
				return false;
			foreach (char c in input)
			{
				if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
					return false;
			}
			normalized = input;
			return true;
		}
		/// <summary>
		/// Returns true if the (normalized) key satisfies the strict key format.
		/// </summary>
		/// <param name="normalizedKey">A normalized key.</param>
		/// <returns></returns>
		public static bool IsStrictKey(string normalizedKey)
		{
			return normalizedKey != null && normalizedKey.Length == StrictKeyLength && Base32.IsBase32String(normalizedKey, StrictKeyLength, StrictKeyLength) && normalizedKey == normalizedKey.ToLowerInvariant();
		}
		/// <summary>
		/// <para>Returns the file name (without extension) used to store the value of the given (normalized) key.</para>
		/// <para>Strict keys are used as-is.  Other keys may contain upper case letters (which collide on case-insensitive file systems) and dots (which could form "." or ".." path segments), so they are stored under the base32 encoding of their UTF-8 bytes with a prefix that can not occur in a strict key.</para>
		/// </summary>
		/// <param name="normalizedKey">A normalized key.</param>
		/// <returns></returns>
		public static string GetFileStem(string normalizedKey)
		{
			if (IsStrictKey(normalizedKey))
				return normalizedKey;
			return permissiveFileNamePrefix + Base32.Encode(Encoding.UTF8.GetBytes(normalizedKey));
		}
		/// <summary>
		/// Returns the name of the subdirectory which holds the value file for the given file stem (the first two base32 characters of the stem).
		/// </summary>
		/// <param name="fileStem">A file stem returned by <see cref="GetFileStem"/>.</param>
		/// <returns></returns>
		public static string GetShardDirectoryName(string fileStem)
		{
			string s = fileStem.StartsWith(permissiveFileNamePrefix, StringComparison.Ordinal) ? fileStem.Substring(permissiveFileNamePrefix.Length) : fileStem;
			return s.Substring(0, 2);
		}
		/// <summary>
		/// Converts a file stem back into the key it represents.  Returns false if the file stem is not one that <see cref="GetFileStem"/> could have produced.
		/// </summary>
		/// <param name="fileStem">File name without extension.</param>
		/// <param name="key">(Output) The key, or null.</param>
		/// <returns></returns>
		public static bool TryGetKeyFromFileStem(string fileStem, out string key)
		{
			key = null;
			if (fileStem == null)
				return false;
			if (fileStem.StartsWith(permissiveFileNamePrefix, StringComparison.Ordinal))
			{
				if (!Base32.TryDecode(fileStem.Substring(permissiveFileNamePrefix.Length), out byte[] data))
					return false;
				string decoded;
				try
				{
					decoded = new UTF8Encoding(false, true).GetString(data);
				}
				catch (ArgumentException)
				{
					return false;
				}
				if (!TryNormalizeKey(decoded, true, out string normalized) || normalized != decoded || IsStrictKey(decoded))
					return false;
				key = decoded;
				return true;
			}
			if (!IsStrictKey(fileStem))
				return false;
			key = fileStem;
			return true;
		}
		/// <summary>
		/// Returns the metadata ID for the given bucket and key: "&lt;bucket&gt;:&lt;key&gt;".
		/// </summary>
		/// <param name="bucket">Normalized bucket name.</param>
		/// <param name="key">Normalized key.</param>
		/// <returns></returns>
		public static string GetId(string bucket, string key)
		{
			return bucket + ":" + key;
		}
	}
}
