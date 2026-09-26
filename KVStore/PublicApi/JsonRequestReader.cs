using BPUtil;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// <para>A streaming reader for the public API's request bodies, which are flat JSON objects whose member values are strings, numbers, booleans, or null.</para>
	/// <para>One member (the stored value) may be routed to an <see cref="IJsonStringSink"/> as it is read, so that a multi-megabyte string is never held in memory.  All other strings are limited to a small length.  Nested objects and arrays are rejected.</para>
	/// <para>Malformed input causes <see cref="ApiException.BadRequest"/>.  Exceeding the body size limit causes <see cref="ApiException.TooLarge"/>.</para>
	/// </summary>
	public sealed class JsonRequestReader : IDisposable
	{
		/// <summary>
		/// Maximum length of a member name, in bytes.
		/// </summary>
		public const int MaxNameLength = 64;
		/// <summary>
		/// Maximum length of a string value that is not streamed, in bytes.
		/// </summary>
		public const int MaxStringLength = 1024;
		/// <summary>
		/// Maximum number of members in the object.
		/// </summary>
		public const int MaxMembers = 32;
		private const int bufferSize = 16384;
		private readonly Stream stream;
		private readonly long maxBodyBytes;
		private readonly int readTimeoutMs;
		private readonly CancellationToken cancellationToken;
		private byte[] buffer;
		private int pos = 0;
		private int len = 0;
		private bool endOfStream = false;
		private readonly byte[] oneByte = new byte[1];
		/// <summary>
		/// Number of bytes read from the stream so far.
		/// </summary>
		public long BytesRead { get; private set; } = 0;
		/// <summary>
		/// True if the reader has read the stream to its end.
		/// </summary>
		public bool ReachedEndOfStream => endOfStream && pos >= len;

		/// <summary>
		/// Constructs a JsonRequestReader.
		/// </summary>
		/// <param name="stream">Stream containing the request body.  May be null if there is no request body.</param>
		/// <param name="maxBodyBytes">If more than this many bytes are read, <see cref="ApiException.TooLarge"/> is thrown.</param>
		/// <param name="readTimeoutMs">Timeout for each read from the stream, in milliseconds.</param>
		/// <param name="cancellationToken">Cancellation Token</param>
		public JsonRequestReader(Stream stream, long maxBodyBytes, int readTimeoutMs, CancellationToken cancellationToken)
		{
			this.stream = stream;
			this.maxBodyBytes = maxBodyBytes;
			this.readTimeoutMs = readTimeoutMs;
			this.cancellationToken = cancellationToken;
			buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
			if (stream == null)
				endOfStream = true;
		}
		/// <summary>
		/// Returns the buffer to the pool.
		/// </summary>
		public void Dispose()
		{
			byte[] b = buffer;
			buffer = null;
			if (b != null)
				ArrayPool<byte>.Shared.Return(b);
		}

		#region Buffered reading
		/// <summary>
		/// Reads more data into the buffer.  Returns false at the end of the stream.
		/// </summary>
		private async ValueTask<bool> FillAsync()
		{
			if (endOfStream)
				return false;
			pos = 0;
			len = 0;
			int read;
			try
			{
				read = await ByteUtil.ReadAsyncWithTimeout(stream, buffer, 0, bufferSize, readTimeoutMs, cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				throw ApiException.BadRequest();
			}
			if (read <= 0)
			{
				endOfStream = true;
				return false;
			}
			BytesRead += read;
			if (BytesRead > maxBodyBytes)
				throw ApiException.TooLarge();
			len = read;
			return true;
		}
		/// <summary>
		/// Reads one byte, or returns -1 at the end of the stream.
		/// </summary>
		private ValueTask<int> ReadByteAsync()
		{
			if (pos < len)
				return new ValueTask<int>(buffer[pos++]);
			return ReadByteSlowAsync();
		}
		private async ValueTask<int> ReadByteSlowAsync()
		{
			if (!await FillAsync().ConfigureAwait(false))
				return -1;
			return buffer[pos++];
		}
		/// <summary>
		/// Returns the next byte without consuming it, or -1 at the end of the stream.
		/// </summary>
		private async ValueTask<int> PeekByteAsync()
		{
			if (pos >= len && !await FillAsync().ConfigureAwait(false))
				return -1;
			return buffer[pos];
		}
		/// <summary>
		/// Reads the next byte that is not JSON whitespace, or returns -1 at the end of the stream.
		/// </summary>
		private async ValueTask<int> ReadNonWhitespaceAsync()
		{
			while (true)
			{
				int b = await ReadByteAsync().ConfigureAwait(false);
				if (b != ' ' && b != '\t' && b != '\r' && b != '\n')
					return b;
			}
		}
		private async ValueTask<int> RequireByteAsync()
		{
			int b = await ReadByteAsync().ConfigureAwait(false);
			if (b == -1)
				throw ApiException.BadRequest();
			return b;
		}
		#endregion

		/// <summary>
		/// <para>Reads the JSON object.  Returns false if the request body was empty (zero bytes or whitespace only), otherwise true.</para>
		/// <para><paramref name="onMember"/> is called as each member is read, so that the caller can validate members (and abort by throwing an <see cref="ApiException"/>) before the rest of the body is read.</para>
		/// </summary>
		/// <param name="streamedMemberName">Name of the member whose string value should be written to <paramref name="sink"/> instead of being held in memory.  If null, no member is streamed.</param>
		/// <param name="sink">Receives the characters of the streamed member.  If the streamed member's value is a string, it is reported to <paramref name="onMember"/> as <see cref="JsonScalarType.StreamedString"/>.</param>
		/// <param name="onMember">Called with each member name and value.  May be null.</param>
		/// <returns></returns>
		public async Task<bool> ReadObjectAsync(string streamedMemberName, IJsonStringSink sink, Action<string, JsonScalar> onMember)
		{
			int b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
			if (b == -1)
				return false;
			if (b == 0xEF)
			{
				// UTF-8 byte order mark
				if (await RequireByteAsync().ConfigureAwait(false) != 0xBB || await RequireByteAsync().ConfigureAwait(false) != 0xBF)
					throw ApiException.BadRequest();
				b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
			}
			if (b != '{')
				throw ApiException.BadRequest();
			HashSet<string> names = new HashSet<string>();
			b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
			if (b != '}')
			{
				while (true)
				{
					if (b != '"')
						throw ApiException.BadRequest();
					string name = await ReadSmallStringAsync(MaxNameLength).ConfigureAwait(false);
					if (!names.Add(name) || names.Count > MaxMembers)
						throw ApiException.BadRequest();
					if (await ReadNonWhitespaceAsync().ConfigureAwait(false) != ':')
						throw ApiException.BadRequest();
					b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
					JsonScalar value;
					if (b == '"')
					{
						if (sink != null && name == streamedMemberName)
						{
							await ReadStreamedStringAsync(sink).ConfigureAwait(false);
							value = JsonScalar.Streamed;
						}
						else
							value = JsonScalar.FromString(await ReadSmallStringAsync(MaxStringLength).ConfigureAwait(false));
					}
					else if (b == '-' || (b >= '0' && b <= '9'))
						value = await ReadNumberAsync((byte)b).ConfigureAwait(false);
					else if (b == 't')
					{
						await ExpectLiteralAsync("rue").ConfigureAwait(false);
						value = JsonScalar.True;
					}
					else if (b == 'f')
					{
						await ExpectLiteralAsync("alse").ConfigureAwait(false);
						value = JsonScalar.False;
					}
					else if (b == 'n')
					{
						await ExpectLiteralAsync("ull").ConfigureAwait(false);
						value = JsonScalar.Null;
					}
					else
						throw ApiException.BadRequest(); // Objects and arrays are not supported.
					onMember?.Invoke(name, value);
					b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
					if (b == '}')
						break;
					if (b != ',')
						throw ApiException.BadRequest();
					b = await ReadNonWhitespaceAsync().ConfigureAwait(false);
				}
			}
			// Only whitespace may follow the object.
			if (await ReadNonWhitespaceAsync().ConfigureAwait(false) != -1)
				throw ApiException.BadRequest();
			return true;
		}
		private async Task ExpectLiteralAsync(string remainder)
		{
			foreach (char c in remainder)
				if (await ReadByteAsync().ConfigureAwait(false) != c)
					throw ApiException.BadRequest();
		}
		private async Task<JsonScalar> ReadNumberAsync(byte first)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append((char)first);
			while (true)
			{
				int b = await PeekByteAsync().ConfigureAwait(false);
				if ((b >= '0' && b <= '9') || b == '.' || b == 'e' || b == 'E' || b == '+' || b == '-')
				{
					pos++;
					sb.Append((char)b);
					if (sb.Length > 64)
						throw ApiException.BadRequest();
				}
				else
					break;
			}
			if (!double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d))
				throw ApiException.BadRequest();
			return JsonScalar.FromNumber(d);
		}
		/// <summary>
		/// Reads a string (the opening quote has already been read) of at most <paramref name="maxBytes"/> UTF-8 bytes.
		/// </summary>
		private async Task<string> ReadSmallStringAsync(int maxBytes)
		{
			List<byte> bytes = new List<byte>();
			while (true)
			{
				int b = await RequireByteAsync().ConfigureAwait(false);
				if (b == '"')
					break;
				if (b < 0x20)
					throw ApiException.BadRequest();
				if (b == '\\')
				{
					int e = await RequireByteAsync().ConfigureAwait(false);
					switch (e)
					{
						case '"': bytes.Add((byte)'"'); break;
						case '\\': bytes.Add((byte)'\\'); break;
						case '/': bytes.Add((byte)'/'); break;
						case 'b': bytes.Add((byte)'\b'); break;
						case 'f': bytes.Add((byte)'\f'); break;
						case 'n': bytes.Add((byte)'\n'); break;
						case 'r': bytes.Add((byte)'\r'); break;
						case 't': bytes.Add((byte)'\t'); break;
						case 'u':
							{
								int codePoint = await ReadHex4Async().ConfigureAwait(false);
								if (codePoint >= 0xD800 && codePoint <= 0xDBFF)
								{
									// High surrogate; a low surrogate must follow.
									if (await RequireByteAsync().ConfigureAwait(false) != '\\' || await RequireByteAsync().ConfigureAwait(false) != 'u')
										throw ApiException.BadRequest();
									int low = await ReadHex4Async().ConfigureAwait(false);
									if (low < 0xDC00 || low > 0xDFFF)
										throw ApiException.BadRequest();
									codePoint = 0x10000 + ((codePoint - 0xD800) << 10) + (low - 0xDC00);
								}
								else if (codePoint >= 0xDC00 && codePoint <= 0xDFFF)
									throw ApiException.BadRequest();
								bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint)));
								break;
							}
						default:
							throw ApiException.BadRequest();
					}
				}
				else
					bytes.Add((byte)b);
				if (bytes.Count > maxBytes)
					throw ApiException.BadRequest();
			}
			try
			{
				return new UTF8Encoding(false, true).GetString(bytes.ToArray());
			}
			catch (ArgumentException)
			{
				throw ApiException.BadRequest();
			}
		}
		private async Task<int> ReadHex4Async()
		{
			int value = 0;
			for (int i = 0; i < 4; i++)
			{
				int b = await RequireByteAsync().ConfigureAwait(false);
				int digit;
				if (b >= '0' && b <= '9')
					digit = b - '0';
				else if (b >= 'a' && b <= 'f')
					digit = b - 'a' + 10;
				else if (b >= 'A' && b <= 'F')
					digit = b - 'A' + 10;
				else
					throw ApiException.BadRequest();
				value = (value << 4) | digit;
			}
			return value;
		}
		/// <summary>
		/// Reads a string (the opening quote has already been read), passing its characters to the sink.  Characters are passed as raw bytes; escape sequences that produce non-ASCII characters are passed as 0xFF so the sink can reject them.
		/// </summary>
		private async Task ReadStreamedStringAsync(IJsonStringSink sink)
		{
			while (true)
			{
				if (pos >= len && !await FillAsync().ConfigureAwait(false))
					throw ApiException.BadRequest(); // Unterminated string.
				int start = pos;
				while (pos < len)
				{
					byte c = buffer[pos];
					if (c == '"' || c == '\\' || c < 0x20)
						break;
					pos++;
				}
				if (pos > start)
					await sink.WriteAsync(new ReadOnlyMemory<byte>(buffer, start, pos - start)).ConfigureAwait(false);
				if (pos >= len)
					continue;
				byte stop = buffer[pos++];
				if (stop == '"')
				{
					await sink.CompleteAsync().ConfigureAwait(false);
					return;
				}
				if (stop < 0x20)
					throw ApiException.BadRequest();
				// Escape sequence
				int e = await RequireByteAsync().ConfigureAwait(false);
				switch (e)
				{
					case '"': oneByte[0] = (byte)'"'; break;
					case '\\': oneByte[0] = (byte)'\\'; break;
					case '/': oneByte[0] = (byte)'/'; break;
					case 'b': oneByte[0] = (byte)'\b'; break;
					case 'f': oneByte[0] = (byte)'\f'; break;
					case 'n': oneByte[0] = (byte)'\n'; break;
					case 'r': oneByte[0] = (byte)'\r'; break;
					case 't': oneByte[0] = (byte)'\t'; break;
					case 'u':
						int codePoint = await ReadHex4Async().ConfigureAwait(false);
						oneByte[0] = codePoint < 0x80 ? (byte)codePoint : (byte)0xFF;
						break;
					default:
						throw ApiException.BadRequest();
				}
				await sink.WriteAsync(new ReadOnlyMemory<byte>(oneByte, 0, 1)).ConfigureAwait(false);
			}
		}
	}
	/// <summary>
	/// Receives the characters of a streamed JSON string value.
	/// </summary>
	public interface IJsonStringSink
	{
		/// <summary>
		/// Receives the next characters of the string, as bytes.  The memory is only valid until the returned task completes.
		/// </summary>
		/// <param name="chars">Characters of the string.</param>
		/// <returns></returns>
		Task WriteAsync(ReadOnlyMemory<byte> chars);
		/// <summary>
		/// Called when the end of the string is reached.
		/// </summary>
		/// <returns></returns>
		Task CompleteAsync();
	}
	/// <summary>
	/// Type of a <see cref="JsonScalar"/>.
	/// </summary>
	public enum JsonScalarType
	{
		/// <summary>A string held in memory.</summary>
		String,
		/// <summary>A string that was streamed to an <see cref="IJsonStringSink"/>.</summary>
		StreamedString,
		/// <summary>A number.</summary>
		Number,
		/// <summary>true</summary>
		True,
		/// <summary>false</summary>
		False,
		/// <summary>null</summary>
		Null
	}
	/// <summary>
	/// A JSON scalar value.
	/// </summary>
	public class JsonScalar
	{
		/// <summary>Type of the value.</summary>
		public readonly JsonScalarType Type;
		/// <summary>String value, if <see cref="Type"/> is <see cref="JsonScalarType.String"/>.</summary>
		public readonly string StringValue;
		/// <summary>Numeric value, if <see cref="Type"/> is <see cref="JsonScalarType.Number"/>.</summary>
		public readonly double NumberValue;
		private JsonScalar(JsonScalarType type, string stringValue = null, double numberValue = 0)
		{
			Type = type;
			StringValue = stringValue;
			NumberValue = numberValue;
		}
		/// <summary>A streamed string.</summary>
		public static readonly JsonScalar Streamed = new JsonScalar(JsonScalarType.StreamedString);
		/// <summary>true</summary>
		public static readonly JsonScalar True = new JsonScalar(JsonScalarType.True);
		/// <summary>false</summary>
		public static readonly JsonScalar False = new JsonScalar(JsonScalarType.False);
		/// <summary>null</summary>
		public static readonly JsonScalar Null = new JsonScalar(JsonScalarType.Null);
		/// <summary>Creates a string value.</summary>
		public static JsonScalar FromString(string s) => new JsonScalar(JsonScalarType.String, s);
		/// <summary>Creates a numeric value.</summary>
		public static JsonScalar FromNumber(double d) => new JsonScalar(JsonScalarType.Number, null, d);
	}
}
