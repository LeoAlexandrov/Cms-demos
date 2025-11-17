using System;

using HCms.Content.ViewModels;


namespace DemoSite.ViewModels
{
	/// <summary>
	/// Represents a CMS page model, including its associated document, menus, and footer.
	/// </summary>
	/// <remarks>This class provides a structured representation of a CMS page, including its document metadata,
	/// language,  authentication status, and navigational elements such as the main menu, navigation menu, and footer. 
	/// The menus and footer are initialized based on the attributes of the provided document.</remarks>
	public class CmsPageModel
	{
		public Document Document { get; set; }
		public string Language { get => string.IsNullOrEmpty(Document?.Language) ? "en-US" : Document.Language; }
		public bool IsAuthenticated { get; protected set; }
		public MainMenu MainMenu { get; set; }
		public NavigationMenu NavigationMenu { get; set; }
		public Footer Footer { get; set; }


		public CmsPageModel(Document document, bool isAuthenticated)
		{
			Document = document;
			IsAuthenticated = isAuthenticated;

			if (Document != null)
			{
				InitializeMenus();
				InitializeFooter();
			}
			else
			{
				MainMenu = new MainMenu() { Languages = [], Commands = [] };
				NavigationMenu = new NavigationMenu() { Commands = [] };
				Footer = new Footer() { Links = [] };
			}
		}

		/// <summary>
		/// Initializes the main menu and navigation menu view models based on the document's attributes and structure.
		/// </summary>
		/// <remarks>
		/// By convention the CMS document can have 'main-menu' attribute containing json-serialized <see cref="MainMenu"/>.
		/// </remarks>
		void InitializeMenus()
		{
			if (Document.Attributes.TryGetValue("main-menu", out string menu))
			{
				MainMenu = System.Text.Json.JsonSerializer.Deserialize<MainMenu>(menu);

				string currentLink = Document.Url;

				for (int i = 0; i < MainMenu.Commands.Length; i++)
				{
					if (MainMenu.Commands[i].Link == currentLink)
						MainMenu.Commands[i].Inactive = true;

					if (MainMenu.Commands[i].Submenu != null)
						for (int j = 0; j < MainMenu.Commands[i].Submenu.Length; j++)
							if (MainMenu.Commands[i].Submenu[j].Link == currentLink)
								MainMenu.Commands[i].Submenu[j].Inactive = true;

				}
			}
			else
			{
				MainMenu = new MainMenu() { Languages = [], Commands = [] };
			}

			string parentTitle = Document.Breadcrumbs.Length > 1 ? Document.Breadcrumbs[^2].Title : string.Empty;
			var navMenu = new NavigationMenu() { Title = parentTitle };

			if (Document.Siblings.Length != 0)
			{
				int n = Document.Siblings.Length;
				int m;

				var commands = new NavigationMenuItem[n];

				for (int i = 0; i < n; i++)
				{
					commands[i] = new NavigationMenuItem()
					{
						Label = Document.Siblings[i].Title,
						Link = Document.Siblings[i].Url
					};

					if (Document.Id == Document.Siblings[i].Id)
					{
						commands[i].Inactive = true;

						if (Document.Children != null && (m = Document.Children.Length) != 0)
						{
							commands[i].Submenu = new NavigationMenuItem[m];

							for (int j = 0; j < m; j++)
								commands[i].Submenu[j] = new NavigationMenuItem()
								{
									Label = Document.Children[j].Title,
									Link = Document.Children[j].Url
								};
						}
					}
				}

				navMenu.Title = parentTitle;
				navMenu.Commands = commands;
			}
			else
			{
				navMenu.Commands = [];
			}

			NavigationMenu = navMenu;
		}

		void InitializeFooter()
		{
			if (Document.Attributes.TryGetValue("footer", out string footer))
				Footer = System.Text.Json.JsonSerializer.Deserialize<Footer>(footer);
			else
				Footer = new Footer() { Links = [] };
		}

	}
}
