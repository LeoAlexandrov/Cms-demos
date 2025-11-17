using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;


namespace DemoSite.Infrastructure.Middleware
{
	public static class CmsAppBuilderExtension
	{
		struct MediaSettings
		{
			public string Host { get; set; }
			public string StoragePath { get; set; }
		}

		/// <summary>
		/// Configures the application to serve static media files from specified storage path.
		/// </summary>
		/// <remarks>This method enables the application to serve static files from the directory specified in the
		/// <c>StoragePath</c> property of the <paramref name="mediaConfig"/>. The <c>Host</c> property is used to determine
		/// the request path for accessing the static files. If either <c>StoragePath</c> or <c>Host</c> is null or empty,
		/// the method does nothing.</remarks>
		/// <param name="builder">The <see cref="IApplicationBuilder"/> instance to configure.</param>
		/// <param name="mediaConfig">The <see cref="IConfiguration"/> instance containing media settings.
		/// The configuration must include valid values for <c>StoragePath</c> and <c>Host</c>.</param>
		/// <returns>The <see cref="IApplicationBuilder"/> instance, configured to serve static files if the media
		/// settings are valid; otherwise, the original instance.</returns>
		public static IApplicationBuilder UseStaticCmsMedia(this IApplicationBuilder builder, IConfiguration mediaConfig)
		{
			IApplicationBuilder result;

			var settings = mediaConfig.Get<MediaSettings>();

			if (!string.IsNullOrEmpty(settings.StoragePath) && !string.IsNullOrEmpty(settings.Host))
			{
				Uri uri = new(settings.Host[^1] == '/' ? settings.Host[..^1] : settings.Host);

				result = builder.UseStaticFiles(new StaticFileOptions()
				{
					FileProvider = new PhysicalFileProvider(settings.StoragePath),
					RequestPath = uri.LocalPath
				});
			}
			else
			{
				result = builder;
			}

			return result;
		}

		/// <summary>
		/// Adds middleware to the application's request pipeline to retrieve CMS content.
		/// </summary>
		/// <remarks>This middleware processes requests to serve content managed by the CMS. Ensure that the
		/// middleware is registered in the correct order relative to other middleware components:
		/// between UseAuthorization() and MapRazorPages().</remarks>
		/// <param name="builder">The <see cref="IApplicationBuilder"/> instance to configure the middleware.</param>
		/// <returns>The <see cref="IApplicationBuilder"/> instance, enabling further middleware configuration.</returns>
		public static IApplicationBuilder UseCmsContent(this IApplicationBuilder builder)
		{
			return builder.UseMiddleware<CmsContentMiddleware>();
		}
	}

}