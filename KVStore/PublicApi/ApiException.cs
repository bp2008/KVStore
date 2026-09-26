using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// An error which the public API reports to the client as <c>{ "ok": false, "error": "&lt;code&gt;" }</c> with the given HTTP status.
	/// </summary>
	public class ApiException : Exception
	{
		/// <summary>
		/// HTTP status line text, e.g. "404 Not Found".
		/// </summary>
		public readonly string HttpStatus;
		/// <summary>
		/// Error code reported in the "error" field, e.g. "not_found".
		/// </summary>
		public readonly string ErrorCode;
		/// <summary>
		/// If greater than 0, a Retry-After header with this many seconds is sent.
		/// </summary>
		public readonly int RetryAfterSeconds;

		/// <summary>
		/// Constructs an ApiException.
		/// </summary>
		/// <param name="httpStatus">HTTP status line text, e.g. "404 Not Found".</param>
		/// <param name="errorCode">Error code reported in the "error" field.</param>
		/// <param name="retryAfterSeconds">If greater than 0, a Retry-After header with this many seconds is sent.</param>
		public ApiException(string httpStatus, string errorCode, int retryAfterSeconds = 0) : base(httpStatus + " " + errorCode)
		{
			HttpStatus = httpStatus;
			ErrorCode = errorCode;
			RetryAfterSeconds = retryAfterSeconds;
		}

		/// <summary>400 bad_request: Malformed JSON or missing field.</summary>
		public static ApiException BadRequest() => new ApiException("400 Bad Request", "bad_request");
		/// <summary>400 invalid_key: Key fails format validation.</summary>
		public static ApiException InvalidKey() => new ApiException("400 Bad Request", "invalid_key");
		/// <summary>400 invalid_bucket: Bucket name fails format validation.</summary>
		public static ApiException InvalidBucket() => new ApiException("400 Bad Request", "invalid_bucket");
		/// <summary>400 invalid_value: Bad base64.</summary>
		public static ApiException InvalidValue() => new ApiException("400 Bad Request", "invalid_value");
		/// <summary>404 not_found: Key absent or expired.</summary>
		public static ApiException NotFound() => new ApiException("404 Not Found", "not_found");
		/// <summary>404 unknown_bucket: Bucket not configured.</summary>
		public static ApiException UnknownBucket() => new ApiException("404 Not Found", "unknown_bucket");
		/// <summary>413 too_large: Value exceeds the bucket's maximum item size.</summary>
		public static ApiException TooLarge() => new ApiException("413 Content Too Large", "too_large");
		/// <summary>429 rate_limited, with Retry-After.</summary>
		public static ApiException RateLimited(int retryAfterSeconds) => new ApiException("429 Too Many Requests", "rate_limited", Math.Max(1, retryAfterSeconds));
		/// <summary>503 bucket_disabled: Bucket disabled by the administrator.</summary>
		public static ApiException BucketDisabled() => new ApiException("503 Service Unavailable", "bucket_disabled");
		/// <summary>503 storage_full: Hard quota reached and eviction failed.</summary>
		public static ApiException StorageFull() => new ApiException("503 Service Unavailable", "storage_full");
	}
}
