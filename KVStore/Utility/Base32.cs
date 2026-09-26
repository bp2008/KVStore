using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// RFC 4648 base32 encoding using the lower case alphabet "abcdefghijklmnopqrstuvwxyz234567" without padding.  Decoding is case-insensitive.  Used for keys and bucket names.
	/// </summary>
	public static class Base32
	{
		/// <summary>
		/// The RFC 4648 base32 alphabet, in lower case.
		/// </summary>
		public const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
		/// <summary>
		/// Maps an ASCII character to its 5-bit value, or -1 if the character is not in the (case-insensitive) alphabet.
		/// </summary>
		private static readonly sbyte[] decodeMap = CreateDecodeMap();
		private static sbyte[] CreateDecodeMap()
		{
			sbyte[] map = new sbyte[128];
			for (int i = 0; i < map.Length; i++)
				map[i] = -1;
			for (int i = 0; i < Alphabet.Length; i++)
			{
				map[Alphabet[i]] = (sbyte)i;
				map[char.ToUpperInvariant(Alphabet[i])] = (sbyte)i;
			}
			return map;
		}
		/// <summary>
		/// Encodes the data as lower case, unpadded base32.
		/// </summary>
		/// <param name="data">Data to encode.</param>
		/// <returns></returns>
		public static string Encode(byte[] data)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));
			return Encode(data.AsSpan());
		}
		/// <summary>
		/// Encodes the data as lower case, unpadded base32.
		/// </summary>
		/// <param name="data">Data to encode.</param>
		/// <returns></returns>
		public static string Encode(ReadOnlySpan<byte> data)
		{
			StringBuilder sb = new StringBuilder((data.Length * 8 + 4) / 5);
			int buffer = 0;
			int bitsInBuffer = 0;
			foreach (byte b in data)
			{
				buffer = (buffer << 8) | b;
				bitsInBuffer += 8;
				while (bitsInBuffer >= 5)
				{
					bitsInBuffer -= 5;
					sb.Append(Alphabet[(buffer >> bitsInBuffer) & 31]);
				}
				buffer &= (1 << bitsInBuffer) - 1;
			}
			if (bitsInBuffer > 0)
				sb.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 31]);
			return sb.ToString();
		}
		/// <summary>
		/// Decodes unpadded base32 (case-insensitive).  Returns false if the string contains characters outside the alphabet, has a length that no byte sequence encodes to, or has non-zero trailing bits.
		/// </summary>
		/// <param name="str">String to decode.</param>
		/// <param name="data">(Output) Decoded data, or null if decoding failed.</param>
		/// <returns></returns>
		public static bool TryDecode(string str, out byte[] data)
		{
			data = null;
			if (str == null)
				return false;
			// Valid unpadded lengths have a remainder (mod 8) of 0, 2, 4, 5, or 7 characters.
			int remainder = str.Length % 8;
			if (remainder == 1 || remainder == 3 || remainder == 6)
				return false;
			byte[] output = new byte[str.Length * 5 / 8];
			int buffer = 0;
			int bitsInBuffer = 0;
			int outputIndex = 0;
			foreach (char c in str)
			{
				int value = c < 128 ? decodeMap[c] : -1;
				if (value < 0)
					return false;
				buffer = (buffer << 5) | value;
				bitsInBuffer += 5;
				if (bitsInBuffer >= 8)
				{
					bitsInBuffer -= 8;
					output[outputIndex++] = (byte)(buffer >> bitsInBuffer);
				}
				buffer &= (1 << bitsInBuffer) - 1;
			}
			if (buffer != 0)
				return false; // Non-canonical encoding: leftover bits must be zero.
			data = output;
			return true;
		}
		/// <summary>
		/// Returns true if the character is in the base32 alphabet (case-insensitive).
		/// </summary>
		/// <param name="c">Character to test.</param>
		/// <returns></returns>
		public static bool IsBase32Char(char c)
		{
			return c < 128 && decodeMap[c] >= 0;
		}
		/// <summary>
		/// Returns true if every character of the string is in the base32 alphabet (case-insensitive) and the length is within the given range.  Unlike <see cref="TryDecode"/>, this does not require the string to be a canonical encoding of a byte sequence.
		/// </summary>
		/// <param name="str">String to test.</param>
		/// <param name="minLength">Minimum length.</param>
		/// <param name="maxLength">Maximum length.</param>
		/// <returns></returns>
		public static bool IsBase32String(string str, int minLength, int maxLength)
		{
			if (str == null || str.Length < minLength || str.Length > maxLength)
				return false;
			foreach (char c in str)
				if (!IsBase32Char(c))
					return false;
			return true;
		}
	}
}
