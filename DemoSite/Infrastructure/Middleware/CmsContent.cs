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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using DemoSite.Services;


namespace DemoSite.Infrastructure.Middleware
{
	/// <summary>
	/// CMS content retrieval takes place in this middleware.
	/// </summary>
	/// <remarks>The service responsible for this is the injected, scoped <see cref="CmsContentService"/>,
	/// used then in razor pages.</remarks>
	public class CmsContentMiddleware(RequestDelegate next, ILogger<CmsContentMiddleware> logger)
	{
		readonly static ConcurrentDictionary<string, AwaitedResult> AwaitedResults = new();
		
		readonly RequestDelegate _next = next;
		readonly ILogger<CmsContentMiddleware> _logger = logger;

		class AwaitedResult
		{
			public CancellationTokenSource Cts { get; set; }
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

			if (context.Request.Method == "GET" &&
				routeData.Values.TryGetValue("page", out object val) &&
				val is string sVal &&
				sVal == "/Index")
			{
				bool allowCaching = string.IsNullOrEmpty(context.Request.QueryString.Value);

				var cache = content.Cache;
				string host = context.Request.Host.Value;
				string path = CleanPath(context.Request.Path.Value);
				string theme = Theme(context);
				var (cmsRoot, cmsPath) = MapPathBack(host, path);
				string cacheKey = $"{cmsRoot}-{theme}-{cmsPath}";

				if (allowCaching && cache.TryGetValue(cacheKey, out byte[] body))
				{
					/* If the cache contains rendered body of the entire page,
					 * we return it immediately short-circuiting the pipeline.
					 */
#if DEBUG
					_logger.LogInformation("Cache hit '{cacheKey}'", cacheKey);
#endif

					context.Response.Headers.ContentType = "text/html; charset=utf-8";

					await context.Response.Body.WriteAsync(body);
				}
				else
				{
					/* Otherwise we need to render the page. */

					AwaitedResult awaitedResult = new();

					if (allowCaching) 
					{
						/* This code branch prevents/minimizes 'cache stampede'.
						   If some thread has already started rendering the page requested, 
						   the 'AwaitedResults' static concurrent dictionary
						   will contain an element with the 'cacheKey' key.
						   This element has CancellationToken which will be cancelled by that thread, 
						   and byte array with the rendered page body.
						   If no one has started rendering the page yet, 
						   this thread adds its own element 'awaitedResult' to the dictionary,
						   and manages the CancellationToken by itself.
						 */

						awaitedResult.Cts = new();

						var ar = AwaitedResults.GetOrAdd(cacheKey, awaitedResult);

						if (ar.Body != null)
						{
							/* The page has been rendered by another thread, 
							 * because GetOrAdd above returned definitely different element with the 'Body' already set.
							 * No need to await, we can return it immediately. 
							 */

							awaitedResult.Cts.Dispose();

							context.Response.Headers.ContentType = "text/html; charset=utf-8";

							await context.Response.Body.WriteAsync(ar.Body);
							return;
						}

						if (ar != awaitedResult)
						{
							/* The page is still being rendered by another thread
							 * because GetOrAdd above returned different element.
							 * We wait for 100 ms or until the CancellationToken 
							 * of the returned element is cancelled by the rendering thread.
							 */

							awaitedResult.Cts.Dispose();
							awaitedResult.Cts = null;

							try
							{
								await Task.Delay(100, ar.Cts.Token);
							}
							catch (TaskCanceledException)
							{
								/* The CancellationToken was cancelled by another thread.
								 * We check if the page has been cached by testing the 'ar.Body' against null.
								 * 'ar.Body's and cached values are the same.
								 */

								if (ar.Body != null)
								{
									// Return rendered body immediately.

									context.Response.Headers.ContentType = "text/html; charset=utf-8";

									await context.Response.Body.WriteAsync(ar.Body);
									return;
								}
							}
						}
					}

					/* If execution reached this point, 
					 * it means that the page is not in the cache
					 * and this thread needs to render it.
					 */

					int paginatedDocsCount = 5; // hardcoded for now, better to move it to the config or to take it from query params

					int position = context.Request.Query.TryGetValue("p", out var qp) && 
						int.TryParse(qp, out int p) && 
						p > 0 ? (p-1) * paginatedDocsCount : 0;


					var doc = await content.GetDocument(cmsRoot, cmsPath, position, paginatedDocsCount, context.User);

					SetCulture(doc?.Language);

					var originalBody = context.Response.Body;
					using var newBody = new MemoryStream();

					context.Response.Body = newBody;

					await _next(context);

					context.Response.Body = originalBody;

					newBody.Seek(0, SeekOrigin.Begin);
					body = new byte[newBody.Length];
					newBody.Read(body, 0, body.Length);

					/* Now we have the entire page body in the 'body' variable
					   which then will be written to the original response body and cached if possible.
					   Caching is possible if:
					   - the response status code is 200 OK,
					   - the document is published (Status == 1),
					   - the document is not protected by authorization,
					   - the response does not contain Cache-Control header prohibiting caching.
					 */

					allowCaching &= context.Response.StatusCode == (int)HttpStatusCode.OK &&
						doc.Status == 1 &&
						!doc.AuthRequired &&
						(!context.Response.Headers.TryGetValue("Cache-Control", out var s) || s != "max-age=0, no-store");

					if (allowCaching)
					{
#if !DEBUG
						cache.Set(cacheKey, awaitedResult.Body = body);
#else
#endif
						_logger.LogInformation("Cached '{cacheKey}'", cacheKey);
					}

					if (awaitedResult.Cts != null)
					{
						/* Cancel the CancellationToken and 
						 * give a signal to awaiting threads that the page has been rendered. 
						 */

						awaitedResult.Cts.Cancel();
						AwaitedResults.TryRemove(cacheKey, out _);
						awaitedResult.Cts.Dispose();
					}

					// Finally write the body to the response.

					await originalBody.WriteAsync(body);
				}
			}
			else
			{
				await _next(context);
			}
		}
	}

}