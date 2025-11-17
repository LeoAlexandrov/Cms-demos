using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using HCms.Content.Repo;
using HCms.Content.Services;


namespace DemoSite.Services
{
	public static class CmsDIExtension
	{
		/// <summary>
		/// Adds sql content repository, path mapper, content service, and cms notifications subscriber to the specified <see cref="IServiceCollection"/>.
		/// </summary>
		/// <remarks>This method registers the sql content repository, <see cref="DemoPathMapper"/>, and
		/// the <see cref="CmsContentService"/> for dependency injection. Sql content repository queries CMS database directly.</remarks>
		/// <param name="services">The <see cref="IServiceCollection"/> to which the services will be added.</param>
		/// <param name="setupAction">An action to configure db context of the sql repository.</param>
		/// <param name="mediaHost">The base URL or host for media content, used to resolve media paths by the path mapper.</param>
		/// <returns>The updated <see cref="IServiceCollection"/> instance.</returns>
		public static IServiceCollection AddCmsContent(this IServiceCollection services, Action<DbContextOptionsBuilder> setupAction, string mediaHost)
		{
			return services
				.AddSingleton<IPathMapper, DemoPathMapper>(s => new DemoPathMapper(mediaHost))
				.AddCmsContentRepo(setupAction)
				.AddScoped<CmsContentService>()
				.AddHostedService<EventSubscriptionService>();
;
		}

		/// <summary>
		/// Adds remote content repository, content service, and cms notifications subscriber to the specified <see cref="IServiceCollection"/>.
		/// </summary>
		/// <remarks>This method registers the remote content repository and the <see cref="CmsContentService"/> as
		/// services in the dependency injection container. The repository configuration is provided via the <paramref
		/// name="remoteRepoSection"/> parameter. CMS backend serves as the content provider.</remarks>
		/// <param name="services">The <see cref="IServiceCollection"/> to which the CMS content services will be added.</param>
		/// <param name="remoteRepoSection">The configuration section containing settings for the remote CMS content repository.</param>
		/// <returns>The updated <see cref="IServiceCollection"/> instance.</returns>
		public static IServiceCollection AddCmsContent(this IServiceCollection services, IConfiguration remoteRepoSection)
		{
			return services
				.AddCmsContentRepo(remoteRepoSection)
				.AddScoped<CmsContentService>()
				.AddHostedService<EventSubscriptionService>();
		}
	}
}