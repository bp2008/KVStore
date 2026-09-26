using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// <para>Incremental base64 decoder.  Input may be supplied in pieces of any size.</para>
	/// <para>Accepts the standard alphabet ("+/") and the URL-safe alphabet ("-_"), with or without "=" padding.  Whitespace is ignored.</para>
	/// </summary>
	public class Base64StreamDecoder
	{
		/// <summary>
		/// Maps an ASCII byte to its 6-bit value.  -1: invalid.  -2: whitespace (ignored).  -3: padding.
		/// </summary>
		private static readonly sbyte[] decodeMap = CreateDecodeMap();
		private static sbyte[] CreateDecodeMap()
		{
			sbyte[] map = new sbyte[256];
			for (int i = 0; i < map.Length; i++)
				map[i] = -1;
			const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
			for (int i = 0; i < alphabet.Length; i++)
				map[alphabet[i]] = (sbyte)i;
			map['-'] = 62;
			map['_'] = 63;
			map[' '] = map['\t'] = map['\r'] = map['\n'] = -2;
			map['='] = -3;
			return map;
		}
		private int accumulator = 0;
		private int charsInQuantum = 0;
		private int paddingChars = 0;
		/// <summary>
		/// Gets the total number of decoded bytes produced so far.
		/// </summary>
		public long BytesDecoded { get; private set; } = 0;
		/// <summary>
		/// Returns the maximum number of bytes that <see cref="Decode"/> could produce for an input of the given length.
		/// </summary>
		/// <param name="inputLength">Number of input characters.</param>
		/// <returns></returns>
		public static int GetMaxOutputLength(int inputLength)
		{
			return (inputLength / 4 + 1) * 3;
		}
		/// <summary>
		/// Decodes a piece of base64 input (ASCII bytes).  Returns the number of bytes written to <paramref name="output"/>, which must be at least <see cref="GetMaxOutputLength"/>(input.Length) long.
		/// </summary>
		/// <param name="input">Base64 characters as ASCII bytes.</param>
		/// <param name="output">Buffer to receive decoded bytes.</param>
		/// <returns>The number of bytes written to <paramref name="output"/>.</returns>
		/// <exception cref="FormatException">If the input is not valid base64.</exception>
		public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
		{
			int written = 0;
			foreach (byte b in input)
			{
				int v = decodeMap[b];
				if (v >= 0)
				{
					if (paddingChars > 0)
						throw new FormatException("Base64 data continues after padding.");
					accumulator = (accumulator << 6) | v;
					if (++charsInQuantum == 4)
					{
						output[written++] = (byte)(accumulator >> 16);
						output[written++] = (byte)(accumulator >> 8);
						output[written++] = (byte)accumulator;
						accumulator = 0;
						charsInQuantum = 0;
					}
				}
				else if (v == -2)
					continue;
				else if (v == -3)
				{
					// Padding completes a quantum that has 2 or 3 data characters.
					paddingChars++;
					if (charsInQuantum < 2 || charsInQuantum + paddingChars > 4)
						throw new FormatException("Invalid base64 padding.");
					if (charsInQuantum + paddingChars == 4)
						written += FlushPartialQuantum(output.Slice(written));
				}
				else
					throw new FormatException("Invalid base64 character.");
			}
			BytesDecoded += written;
			return written;
		}
		/// <summary>
		/// Finishes decoding.  Returns the number of final bytes written to <paramref name="output"/> (at most 2).
		/// </summary>
		/// <param name="output">Buffer to receive decoded bytes.  Must be at least 2 bytes long.</param>
		/// <returns>The number of bytes written to <paramref name="output"/>.</returns>
		/// <exception cref="FormatException">If the input ended in the middle of a quantum.</exception>
		public int Finish(Span<byte> output)
		{
			int written = 0;
			if (paddingChars > 0)
			{
				if (charsInQuantum != 0)
					throw new FormatException("Base64 data ended with incomplete padding.");
			}
			else if (charsInQuantum == 1)
				throw new FormatException("Base64 data ended with an incomplete quantum.");
			else if (charsInQuantum > 1)
				written = FlushPartialQuantum(output);
			BytesDecoded += written;
			return written;
		}
		/// <summary>
		/// Writes the bytes represented by a quantum of 2 or 3 data characters and resets the quantum.
		/// </summary>
		private int FlushPartialQuantum(Span<byte> output)
		{
			int written;
			if (charsInQuantum == 2)
			{
				output[0] = (byte)(accumulator >> 4);
				written = 1;
			}
			else
			{
				output[0] = (byte)(accumulator >> 10);
				output[1] = (byte)(accumulator >> 2);
				written = 2;
			}
			accumulator = 0;
			charsInQuantum = 0;
			return written;
		}
	}
}
