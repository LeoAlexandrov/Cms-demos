using System;

using HCms.Content.Services;


namespace DemoSite.Services
{
	/*
	 * Path mapper (IPathMapper implementation) is the essential part if content consuming site 
	 * uses sql-based repository querying CMS database directly.
	 * You don't need it if you use remote repository or use gRPC, which get content from the CMS backend via API.
	 * 
	 * Path mapper is responsible for URL generation, i.e. for mapping document paths and media paths 
	 * stored in CMS database to the desired URLs.
	 * 
	 * This DemoPathMapper is tailored for the default CMS demo content organized as two separate document trees: 
	 * English and French, having roots named 'home' and 'home-fr' respectively.
	 * The desired demo site map structure is to have French content under '/fr' path segment.
	 * 
	 * Media content can be served by nginx or by the web application itself.
	 * In both cases you need to provide the base URL or host for media content to the path mapper.
	 * 
	 * If you use nginx:
	 * Let /var/www/media/mysite.com folder contains media files for mysite.com,
	 * and nginx is configured somehow to serve media.mysite.com from that folder, e.g.:
	  
		server {
			listen 443 ssl;
			listen [::]:443 ssl;
			server_name media.mysite.com;
			root /var/www/media/mysite.com;
			location / {
				try_files $uri $uri/ =404;
			}
			ssl_certificate /etc/letsencrypt/....
			ssl_certificate_key /etc/letsencrypt/...
		}

	 * then you need to provide "https://media.mysite.com" as media host to the path mapper.
	 * 
	 * If you'd like use the web application to serve media files itself, then you need to use UseStaticFiles()
	 * configured with StaticFileOptions where you specify RequestPath and FileProvider pointing to the media folder.
	 * Provide RequestPath as media host to the path mapper. See CmsAppBuilderExtension.UseStaticCmsMedia() implementation.
	 * 
	 * 
	 * If you use remote repository or gRPC, CMS backend has its own configured named path mappers based on regex replacement.
	 * We'll cover this in CMS backend documentation.
	 */


	public class DemoPathMapper : IPathMapper
	{
		const string HTTPS = "https://";
		const string HTTP = "http://";
		const string DEFAULT_ROOT_FR = "home-fr";

		private readonly Uri baseMediaHost;

		public DemoPathMapper(string mediaHost)
		{
			string host = mediaHost;

			if (string.IsNullOrEmpty(host))
				host = "/";
			else if (host[^1] != '/')
				host += "/";

			baseMediaHost = new Uri(host);
		}


		public string Map(string root, string path, bool media = false)
		{
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("Path is required for mapping.", nameof(path));

			if (path.StartsWith(HTTPS, StringComparison.OrdinalIgnoreCase) ||
				path.StartsWith(HTTP, StringComparison.OrdinalIgnoreCase))
				return path;

			if (string.IsNullOrWhiteSpace(root) && !media)
				throw new ArgumentNullException(nameof(root), "Root document slug is required for mapping.");

			if (media)
			{
				var mediaUri = new Uri(baseMediaHost, path);
				return mediaUri.ToString();
			}

			if (string.Equals(root, DEFAULT_ROOT_FR, StringComparison.OrdinalIgnoreCase))
			{
				return "/fr" + path;

				// return "https://fr.mysite.com" + path;
				// if we'd like to use separate domain for French content
			}

			return path;
		}
	}
}
