using System;
using System.Collections.Generic;
using HCms.Content.ViewModels;

namespace DemoSite.ViewModels
{

	public struct PaginationLink : ILink
	{
		public string Label { get; set; }
		public string Link { get; set; }
	}

}