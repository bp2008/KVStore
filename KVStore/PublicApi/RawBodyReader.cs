using BPUtil;
using BPUtil.IO;
using BPUtil.SimpleHttp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KVStore
{
	/// <summary>
	/// Streams a request body that is the stored value itself (the "putraw" operation) to a file, without holding it in memory.
	/// </summary>
	public sealed class RawBodyReader
	{
		private const int bufferSize = 65536;
		private readonly Stream stream;
		private readonly int readTimeoutMs;
		private readonly CancellationToken cancellationToken;
		/// <summary>
		/// Number of bytes read from the stream so far.
		/// </summary>
		public long BytesRead { get; private set; } = 0;
		/// <summary>
		/// True if the reader has read the stream to its end.
		/// </summary>
		public bool ReachedEndOfStream { get; private set; } = false;

		/// <summary>
		/// Constructs a RawBodyReader.
		/// </summary>
		/// <param name="stream">Stream containing the request body.  May be null if there is no request body.</param>
		/// <param name="readTimeoutMs">Timeout for each read from the stream, in milliseconds.</param>
		/// <param name="cancellationToken">Cancellation Token</param>
		public RawBodyReader(Stream stream, int readTimeoutMs, CancellationToken cancellationToken)
		{
			this.stream = stream;
			this.readTimeoutMs = readTimeoutMs;
			this.cancellationToken = cancellationToken;
		}

		/// <summary>
		/// Writes the entire request body to a new file and flushes it to disk.  Throws <see cref="ApiException.TooLarge"/> as soon as more than <paramref name="limit"/> bytes arrive, or <see cref="ApiException.BadRequest"/> if the body stalls or ends early.
		/// </summary>
		/// <param name="filePath">Path of the file to create.  It must not exist.</param>
		/// <param name="limit">Maximum body size in bytes.</param>
		/// <returns></returns>
		public async Task CopyToFileAsync(string filePath, long limit)
		{
			byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
			try
			{
				using (FileStream fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous))
				{
					while (stream != null)
					{
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
							break;
						BytesRead += read;
						if (BytesRead > limit)
							throw ApiException.TooLarge();
						await fs.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
					}
					// BPUtil's body streams report the end of the underlying connection as an ordinary end of stream, so a truncated upload must be detected here, or it would be stored as if it were complete.
					if ((stream is Substream ss && !ss.EndOfStream) || (stream is ReadableChunkedTransferEncodingStream cs && !cs.EndOfStream))
						throw ApiException.BadRequest();
					ReachedEndOfStream = true;
					await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
					fs.Flush(true); // fsync, so the data is durable before the file is moved into place and its metadata is written.
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}
}
