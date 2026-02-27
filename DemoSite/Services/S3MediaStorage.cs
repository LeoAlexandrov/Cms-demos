using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using HCms.Dto;
using DemoSite.Infrastructure.Middleware;


namespace DemoSite.Services
{
	public class S3Settings
	{
		public string Endpoint { get; set; }
		public string Name { get; set; }
		public string AccessKey { get; set; }
		public string SecretKey { get; set; }
		public string CacheFolder { get; set; }
		public string[] FileTypesToCache { get; set; }
	}



	public sealed class S3StreamWrapper : Stream
	{
		readonly GetObjectResponse _response;
		readonly Stream _stream;
		bool _disposed;

		public S3StreamWrapper(GetObjectResponse response)
		{
			ArgumentNullException.ThrowIfNull(response);

			_response = response;
			_stream = response.ResponseStream;
		}

		public override bool CanRead => _stream.CanRead;
		public override bool CanSeek => _stream.CanSeek;
		public override bool CanWrite => _stream.CanWrite;
		public override long Length => _stream.Length;
		public override long Position { get => _stream.Position; set => _stream.Position = value; }

		public override void Flush() => _stream.Flush();
		public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
		public override void SetLength(long value) => _stream.SetLength(value);
		public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

		protected override void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					_response.Dispose();
				}

				_disposed = true;
			}

			base.Dispose(disposing);
		}
	}



	public class S3MediaStorage
	{
		const string EVENT_MEDIA_CREATE = "on_media_create";
		const string EVENT_MEDIA_DELETE = "on_media_delete";

		private readonly static ConcurrentDictionary<string, AwaitedResult> AwaitedResults = new();

		public S3Settings Settings { get; private set; }
		private readonly AmazonS3Client _client;
		private readonly ILogger<CmsContentMiddleware> _logger;
		private readonly HashSet<string> _fileTypesToCache;


		public enum Status
		{
			Success,
			NotFound,
			OtherProblem
		}

		public struct Result
		{
			public Status Status { get; set; }
			public string FileName { get; set; }
			public string DownloadName { get; set; }
			public string ContentType { get; set; }
			public long Size { get; set; }
			public Stream Content { get; set; }

		}

		class AwaitedResult
		{
			public CancellationTokenSource Cts { get; set; }
			public string FileName { get; set; }
			public string ContentType { get; set; }
		}

		static string ContentType(string fileName)
		{
			var provider = new FileExtensionContentTypeProvider();

			if (provider.TryGetContentType(fileName, out string contentType))
				return contentType;

			return "application/octet-stream";
		}

		public S3MediaStorage(IOptions<S3Settings> settings, ILogger<CmsContentMiddleware> logger)
		{
			Settings = settings.Value;
			_logger = logger;
			_fileTypesToCache = [.. Settings.FileTypesToCache ?? []];

			if (!string.IsNullOrEmpty(Settings.CacheFolder))
				Directory.CreateDirectory(Settings.CacheFolder);

			if (!string.IsNullOrEmpty(Settings.AccessKey) &&
				!string.IsNullOrEmpty(Settings.SecretKey) &&
				!string.IsNullOrEmpty(Settings.Endpoint))
			{
				var creds = new BasicAWSCredentials(Settings.AccessKey, Settings.SecretKey);

				var config = new AmazonS3Config()
				{
					ServiceURL = $"https://{Settings.Endpoint}",
					ForcePathStyle = true
				};

				_client = new AmazonS3Client(creds, config);
			}
			else 
			{
				_logger.LogError("S3 client misconfigured. At least one of 'AccessKey', 'SecretKey', 'Endpoint' is null or empty.");
			}
		}

		public async Task<Result> GetMediaFileAsync(string path)
		{
			if (_client == null)
			{
				_logger.LogError("S3 client misconfigured.");

				return new Result() { Status = Status.OtherProblem };
			}

			string file = Path.GetFileName(path);
			string fileType = Path.GetExtension(file);
			Result result;

			if (!_fileTypesToCache.Contains(fileType))
			{

				try
				{
					var request = new GetObjectRequest()
					{
						BucketName = Settings.Name,
						Key = path
					};

					var response = await _client.GetObjectAsync(request);

					result = new Result()
					{
						ContentType = ContentType(file),
						DownloadName = file,
						Size = response.ContentLength,
						Content = new S3StreamWrapper(response) // will be disposed by asp.net
					};
				}
				catch (AmazonS3Exception ex)
				{
					_logger.LogError(ex, "AWSS3: Failed to retrieve file '{path}'", path);
					result = new Result() { Status = ex.StatusCode == System.Net.HttpStatusCode.NotFound ? Status.NotFound : Status.OtherProblem };
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to retrieve file '{path}'", path);
					result = new Result() { Status = Status.OtherProblem };
				}

				return result;
			}


			string folder = Path.GetDirectoryName(path);

			if (Path.DirectorySeparatorChar != '/')
				folder = folder.Replace('/', Path.DirectorySeparatorChar);

			string resultName = Path.Combine(Settings.CacheFolder, folder, file);

			// prevent cache stampede

			var awaitedResult = new AwaitedResult() { Cts = new() };
			var ar = AwaitedResults.GetOrAdd(resultName, awaitedResult);

			if (ar.FileName != null)
			{
				awaitedResult.Cts.Dispose();
				return new Result() { FileName = ar.FileName, ContentType = ar.ContentType };
			}

			if (ar != awaitedResult)
			{
				awaitedResult.Cts.Dispose();
				awaitedResult.Cts = null;

				try
				{
					await Task.Delay(500, ar.Cts.Token);
				}
				catch (TaskCanceledException)
				{
					/* The CancellationToken was cancelled by another thread. */

					if (ar.FileName != null)
						return new Result() { FileName = ar.FileName, ContentType = ar.ContentType };
				}
			}

			// end

			try
			{
				if (!string.IsNullOrEmpty(folder))
					Directory.CreateDirectory(Path.Combine(Settings.CacheFolder, folder));

				var request = new GetObjectRequest()
				{
					BucketName = Settings.Name,
					Key = path
				};

				using var response = await _client.GetObjectAsync(request);

				await response.WriteResponseStreamToFileAsync(resultName, false, default);

				string cType = ContentType(file);

				if (awaitedResult.Cts != null)
				{
					awaitedResult.FileName = resultName;
					awaitedResult.ContentType = cType;

					/* Cancel the CancellationToken and 
					 * give a signal to awaiting threads that the file has been retrieved. 
					 */

					awaitedResult.Cts.Cancel();
					AwaitedResults.TryRemove(resultName, out _);
					awaitedResult.Cts.Dispose();
				}

				result = new Result() { FileName = resultName, ContentType = cType };
			}
			catch (AmazonS3Exception ex)
			{
				_logger.LogError(ex, "AWS: Failed to retrieve file '{path}'", path);
				result = new Result() { Status = ex.StatusCode == System.Net.HttpStatusCode.NotFound ? Status.NotFound : Status.OtherProblem };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to retrieve file '{path}'", path);
				result = new Result() { Status = Status.OtherProblem };
			}


			return result;
		}

		/// <summary>
		/// Updates the media cache basing on notication event payload. Used by <see cref="EventSubscriptionService"/>
		/// </summary>
		/// <param name="model">The event payload containing the event type and any associated data. 
		/// The <see cref="EventPayload.Event"/> property determines the type of cache update to perform.</param>
		public void UpdateCache(EventPayload model)
		{
			switch (model.Event)
			{
				case EVENT_MEDIA_CREATE:
				case EVENT_MEDIA_DELETE:

					if (model.AffectedContent != null)
					{
						string cacheFolder = Settings.CacheFolder;

						foreach (var con in model.AffectedContent)
						{
							int i = con.Path.IndexOf('/') + 1;

							string name = Path.Combine(
								cacheFolder, 
								Path.DirectorySeparatorChar == '/' ? con.Path[i..] : con.Path[i..].Replace('/', Path.DirectorySeparatorChar));

							try
							{
								if (File.Exists(name))
									File.Delete(name);
								else if (Directory.Exists(name))
									Directory.Delete(name, true);
							}
							catch (Exception ex)
							{
								_logger.LogError(ex, "Failed to remove cached media '{name}'", name);
							}
						}

						_logger.LogInformation("Cached media have been removed");
					}

					break;

				default:
					break;
			}
		}

	}
}