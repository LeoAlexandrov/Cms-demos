# Demo Site Project

Here is a live example of this project: [https://demo.h-cms.net](https://demo.h-cms.net).

With [default configuration](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/settings.hcms-demo.json),
this app retrieves content from the [public H-Cms instance](https://admin.h-cms.net) and displays exactly what demo.h-cms.net does. This H-Cms instance provides public API key used in all demo projects of this repository.

To run this project with your own data, follow these steps:

* Clone [H-CMS repository](https://github.com/LeoAlexandrov/Cms).
* Set up H-Cms locally by following the [instructions](https://github.com/LeoAlexandrov/Cms#prerequisites); actually you don't even need to setup CMS authorization, it is enough to have a running instance of H-Cms with populated database demo content. Important is to set database connection string and to provide media storage path.
* Set the ["UseSettingsFile" parameter](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/appsettings.Development.json#L11) of the appsettings.Development.json file to `"settings.remote-repo.json"`.
* Set the ["Media:StoragePath"](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/settings.remote-repo.json#L10) with the same value you set in the H-Cms configuration file.
* Start H-CMS, then start this DemoSite project.

The steps above configures the DemoSite to use H-CMS as the remote repository and to retrieve content using the REST requests. If you want to query the database directly, run H-CMS at least once to seed demo content, and follow these steps:

* Set the ["UseSettingsFile" parameter](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/appsettings.Development.json#L11) of the appsettings.Development.json file to `"settings.sql-repo.json"`.
* Set the connection string in the [settings.sql-repo.json](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/settings.sql-repo.json#L5) file.
* Set the ["Media:StoragePath"](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/settings.sql-repo.json#L10) with the same value you set in the H-Cms configuration file.
* Fix [these lines of code](https://github.com/LeoAlexandrov/Cms-demos/blob/master/DemoSite/Program.cs#L88) as written there.

**Note**: no need to have H-CMS running in this case, as the DemoSite will connect to the same database directly.