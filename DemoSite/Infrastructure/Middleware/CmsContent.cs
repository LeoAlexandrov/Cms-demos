using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

using DemoSite.Services;


namespace DemoSite.Infrastructure.Middleware
{
	/// <summary>
	/// CMS content retrieval takes place in this middleware.
	/// </summary>
	/// <remarks>The service responsible for this is the injected, scoped <see cref="CmsContentService"/>,
	/// used then in razor pages.</remarks>
	public class CmsContentMiddleware(RequestDelegate next, HybridCache cache, ILogger<CmsContentMiddleware> logger)
	{
		readonly static HybridCacheEntryOptions CacheWriteOptions = new()
		{
			Expiration = TimeSpan.FromDays(60),
			LocalCacheExpiration = TimeSpan.FromDays(30)
		};

		readonly static HybridCacheEntryOptions CacheReadThroughOptions = new()
		{
			Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite
		};

		readonly static string[] tags = [CmsContentService.CMS_CACHE_TAG];

		readonly RequestDelegate _next = next;
		readonly HybridCache _cache = cache;
		readonly ILogger<CmsContentMiddleware> _logger = logger;

		struct RenderedDocument
		{
			public HCms.Content.ViewModels.Document Document { get; set; }
			public byte[] Body { get; set; }
		}


		/// <summary>
		/// Removes duplicate slashes, trailing slash, and converts to lowercase.
		/// </summary>
		/// <param name="path">Path</param>
		/// <returns>Cleaned path</returns>
		static string CleanPath(string path)
		{
			if (string.IsNullOrEmpty(path) || path.All(c => c == '/'))
				return "/";

			var result = new StringBuilder(path.Length + 1);
			var prevChar = '\0';

			if (path[0] != '/')
				result.Append('/');

			for (int i = 0; i < path.Length; i++)
			{
				var c = path[i];

				if (c != '/' || prevChar != '/')
					result.Append(char.ToLower(c));

				prevChar = c;
			}

			if (result[^1] == '/')
				result.Length--;

			return result.ToString();
		}

		static void SetCulture(string lang)
		{
			if (!string.IsNullOrEmpty(lang) && 
				!lang.StartsWith(Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName))
			{
				var docCulture = new CultureInfo(lang);
				Thread.CurrentThread.CurrentCulture = docCulture;
				Thread.CurrentThread.CurrentUICulture = docCulture;
			}
		}

		static string Theme(HttpContext context)
		{
			return context.Request.Cookies["Theme"] ?? "light";
		}

		/// <summary>
		/// This method performs reverse mapping of the request path
		/// </summary>
		/// <param name="host">Host</param>
		/// <param name="path">Path</param>
		/// <returns>CMS root and CMS path</returns>
		static (string, string) MapPathBack(string host, string path)
		{
			const string DEFAULT_ROOT = "home";
			const string DEFAULT_ROOT_FR = "home-fr";

			var rx = new Regex(@"^/(fr)(/{1}.*)?$"); // todo: make it compile-time implemented with [GeneratedRegex]
			var mappedPath = rx.Replace(path, "$2");

			if (string.IsNullOrEmpty(mappedPath))
				mappedPath = "/";

			if (mappedPath != path)
				return (DEFAULT_ROOT_FR, mappedPath);

			return (DEFAULT_ROOT, path);
		}

		public async Task InvokeAsync(HttpContext context, CmsContentService content)
		{
			var routeData = context.GetRouteData();

			if (context.Request.Method == HttpMethods.Get &&
				routeData.Values.TryGetValue("page", out object val) &&
				val is string sVal &&
				sVal == "/Index")
			{
				bool allowCaching = string.IsNullOrEmpty(context.Request.QueryString.Value);

				string host = context.Request.Host.Value;
				string path = CleanPath(context.Request.Path.Value);
				string theme = Theme(context);

				var (cmsRoot, cmsPath) = MapPathBack(host, path);

				string cacheKey = $"{cmsRoot}-{theme}-{cmsPath}";

				int paginatedDocsCount = 5; // hardcoded for now, better to move it to the config or to take it from query params

				int position = context.Request.Query.TryGetValue("p", out var qp) &&
					int.TryParse(qp, out int p) &&
					p > 1 ? (p - 1) * paginatedDocsCount : 0;


				async Task<RenderedDocument> getAndRenderDocument(CancellationToken ct)
				{
					var doc = await content.GetDocument(cmsRoot, cmsPath, position, paginatedDocsCount, context.User, ct);

					SetCulture(doc?.Language);

					using var ms = new MemoryStream();
					context.Response.Body = ms;

					await _next(context);

					var body = new byte[ms.Length];

					ms.Seek(0, SeekOrigin.Begin);
					ms.Read(body, 0, body.Length);

					return new() { Document = doc, Body = body };
				}

				async ValueTask<byte[]> factory(CancellationToken ct)
				{
					var renderedDoc = await getAndRenderDocument(ct);

					allowCaching &= context.Response.StatusCode == (int)HttpStatusCode.OK &&
						renderedDoc.Document.Status == 1 &&
						!renderedDoc.Document.AuthRequired &&
						(!context.Response.Headers.TryGetValue("Cache-Control", out var s) || s != "max-age=0, no-store");

					if (allowCaching)
					{
						await _cache.SetAsync(
							cacheKey,
							renderedDoc.Body,
							options: CacheWriteOptions,
							tags: tags,
							cancellationToken: ct);

#if DEBUG
						_logger.LogInformation("Cached '{cacheKey}'", cacheKey);
#endif
					}

					return renderedDoc.Body;
				}

				var originalBody = context.Response.Body;

				var body = allowCaching ?
					await _cache.GetOrCreateAsync(cacheKey, factory, CacheReadThroughOptions, cancellationToken: context.RequestAborted) :
					await getAndRenderDocument(context.RequestAborted).ContinueWith(t => t.Result.Body, context.RequestAborted);

				context.Response.Body = originalBody;
				context.Response.Headers.ContentType = "text/html; charset=utf-8";

				await originalBody.WriteAsync(body);
			}
			else
			{
				await _next(context);
			}
		}
	}

}