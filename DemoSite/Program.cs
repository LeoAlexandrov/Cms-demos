using System;
using System.Globalization;
using System.IO;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DemoSite.Infrastructure.Middleware;
using DemoSite.Services;


void LoadConfiguration(ConfigurationManager configuration)
{
	string settingsFile = Environment.GetEnvironmentVariable("SETTINGS");

	if (!string.IsNullOrEmpty(settingsFile))
	{
		if (settingsFile.StartsWith("../"))
			settingsFile = Path.GetFullPath(settingsFile);

		if (File.Exists(settingsFile))
		{
			configuration.AddJsonFile(settingsFile, true);
			return;
		}
	}

	settingsFile = configuration["UseSettingsFile"];

	if (!string.IsNullOrEmpty(settingsFile))
		configuration.AddJsonFile(settingsFile.StartsWith("../") ? Path.GetFullPath(settingsFile) : settingsFile, true);
}

void ConfigureWebHost(IWebHostBuilder webHostBuilder)
{
	/*
	 * Kestrel configuration assumes "Kestrel" section in appsettings.json
	 * https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0
	 * ----
		"Kestrel": {
			"Endpoints": {
				"Http": {
					"Url": "http://localhost:8085"
				}
			}
		},
	 * ----
	 */

	webHostBuilder.ConfigureKestrel((context, options) =>
		options.Configure(context.Configuration.GetSection("Kestrel")));

}

void ConfigureDatabase(DbContextOptionsBuilder options, IConfiguration configuration)
{
	string dbEngine = configuration["DbEngine"];
	string connString = configuration.GetConnectionString("CmsDbConnection");

	if (string.IsNullOrEmpty(dbEngine) || dbEngine == "mssql")
		options.UseSqlServer(connString);
	// add package 'Npgsql.EntityFrameworkCore.PostgreSQL' if you need Postgres support
	// and uncomment lines below
	/*
	else if (dbEngine == "postgres")
		options.UseNpgsql(connString);
	*/
	// add package 'MySql.EntityFrameworkCore' if you need MySQL support
	// and uncomment lines below
	/*
	else if (dbEngine == "mysql")
		options.UseMySQL(connString);
	*/
	else
		throw new NotSupportedException($"Database engine '{dbEngine}' is not supported.");
}

void ConfigureServices(IServiceCollection services, ConfigurationManager configuration)
{
	services
		.AddMemoryCache()
		/*
		// the line below configures demo site to use sql database as the content repository
		.AddCmsContent(options => ConfigureDatabase(options, configuration), configuration["Media:Host"])
		*/
		/*
		// two lines below configure demo site to use remote cms content repository
		*/
		.AddHttpClient()
		.AddCmsContent(configuration.GetSection("RemoteRepo"))
		.AddLocalization(options => options.ResourcesPath = "Resources")
		.AddRazorPages(options => options.Conventions.AddPageRoute("/index", "{*url}"))
		.AddViewLocalization();
}

void ConfigureApp(WebApplication app)
{
	var supportedCultures = new[]
	{
		new CultureInfo("en"),
		new CultureInfo("fr")
	};

	var localizationOptions = new RequestLocalizationOptions
	{
		DefaultRequestCulture = new RequestCulture("en"),
		SupportedCultures = supportedCultures,
		SupportedUICultures = supportedCultures,
	};

	if (app.Environment.IsDevelopment())
	{
		app.UseDeveloperExceptionPage();
	}
	else
	{
		app.UseExceptionHandler("/Error");
		// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
		app.UseHsts();
	}


	app.UseForwardedHeaders()
#if DEBUG
		// assumed production deployment configuration is the app running behind a reverse proxy with TLS termination
		.UseHttpsRedirection()
#endif
		.UseStaticFiles()
		.UseStaticCmsMedia(app.Configuration.GetSection("Media"))
		.UseRouting()
		.UseRequestLocalization(localizationOptions)
		.UseAuthentication()
		.UseAuthorization()
		.UseCmsContent();

	app.MapPost("/cms-webhook-handler",
		(HCms.Dto.EventPayload model, CmsContentService cmsService, IConfiguration configuration, HttpRequest request) =>
		{
			string secret = configuration["Webhook:Secret"];

			if (secret == request.Headers["X-Secret"])
				cmsService.UpdateCache(model);

			return Results.NoContent();
		});

	app.UseStatusCodePagesWithReExecute("/Error/{0}");
	app.MapRazorPages();
}



var builder = WebApplication.CreateBuilder(args);

LoadConfiguration(builder.Configuration);
ConfigureWebHost(builder.WebHost);
ConfigureServices(builder.Services, builder.Configuration);


var app = builder.Build();

ConfigureApp(app);

app.Run();