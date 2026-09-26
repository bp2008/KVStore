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
	/// Receives a streamed base64 JSON string, decodes it incrementally, and writes the decoded bytes to a file.  The file is created when the first characters arrive.
	/// </summary>
	public sealed class Base64FileSink : IJsonStringSink, IDisposable
	{
		private const int maxInputChunk = 16384;
		private readonly string filePath;
		private readonly CancellationToken cancellationToken;
		private readonly Base64StreamDecoder decoder = new Base64StreamDecoder();
		private byte[] outputBuffer;
		private FileStream fileStream;
		private long limit;
		/// <summary>
		/// True after the file was created.
		/// </summary>
		public bool FileCreated { get; private set; } = false;
		/// <summary>
		/// True after the end of the string was reached and the file was flushed to disk.
		/// </summary>
		public bool Completed { get; private set; } = false;
		/// <summary>
		/// Number of decoded bytes written.
		/// </summary>
		public long BytesWritten => decoder.BytesDecoded;
		/// <summary>
		/// Maximum number of decoded bytes.  If exceeded, <see cref="ApiException.TooLarge"/> is thrown.  May be lowered while the value is being received (e.g. when the bucket becomes known).
		/// </summary>
		public long Limit
		{
			get
			{
				return limit;
			}
			set
			{
				limit = value;
				if (BytesWritten > limit)
					throw ApiException.TooLarge();
			}
		}

		/// <summary>
		/// Constructs a Base64FileSink.
		/// </summary>
		/// <param name="filePath">Path of the file to create.  It must not exist.</param>
		/// <param name="limit">Maximum number of decoded bytes.</param>
		/// <param name="cancellationToken">Cancellation Token</param>
		public Base64FileSink(string filePath, long limit, CancellationToken cancellationToken)
		{
			this.filePath = filePath;
			this.limit = limit;
			this.cancellationToken = cancellationToken;
			outputBuffer = ArrayPool<byte>.Shared.Rent(Base64StreamDecoder.GetMaxOutputLength(maxInputChunk));
		}
		private void EnsureFile()
		{
			if (fileStream == null)
			{
				fileStream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
				FileCreated = true;
			}
		}
		/// <inheritdoc/>
		public async Task WriteAsync(ReadOnlyMemory<byte> chars)
		{
			EnsureFile();
			while (chars.Length > 0)
			{
				ReadOnlyMemory<byte> chunk = chars.Length > maxInputChunk ? chars.Slice(0, maxInputChunk) : chars;
				chars = chars.Slice(chunk.Length);
				int n;
				try
				{
					n = decoder.Decode(chunk.Span, outputBuffer);
				}
				catch (FormatException)
				{
					throw ApiException.InvalidValue();
				}
				if (decoder.BytesDecoded > limit)
					throw ApiException.TooLarge();
				if (n > 0)
					await fileStream.WriteAsync(outputBuffer, 0, n, cancellationToken).ConfigureAwait(false);
			}
		}
		/// <inheritdoc/>
		public async Task CompleteAsync()
		{
			EnsureFile();
			int n;
			try
			{
				n = decoder.Finish(outputBuffer);
			}
			catch (FormatException)
			{
				throw ApiException.InvalidValue();
			}
			if (decoder.BytesDecoded > limit)
				throw ApiException.TooLarge();
			if (n > 0)
				await fileStream.WriteAsync(outputBuffer, 0, n, cancellationToken).ConfigureAwait(false);
			await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
			fileStream.Flush(true); // fsync, so the data is durable before the file is moved into place and its metadata is written.
			fileStream.Dispose();
			fileStream = null;
			Completed = true;
		}
		/// <summary>
		/// Closes the file (if still open) and returns the buffer to the pool.  Does not delete the file.
		/// </summary>
		public void Dispose()
		{
			try
			{
				fileStream?.Dispose();
			}
			catch { }
			fileStream = null;
			byte[] b = outputBuffer;
			outputBuffer = null;
			if (b != null)
				ArrayPool<byte>.Shared.Return(b);
		}
	}
}
