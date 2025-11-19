using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using MessagePack;

using HCms.Content.ViewModels;


namespace RestDemo
{
	internal class Program
	{
		const string MSGPACK_MEDIA_TYPE = "application/x-msgpack";

		async static Task<T> RestRequest<T>(HttpClient client, string url, string apiKey, string acceptMediaType)
		{
			using HttpRequestMessage request = new()
			{
				Method = HttpMethod.Get,
				RequestUri = new Uri(url)
			};

			request.Headers.Add("APIKey", apiKey);

			if (!string.IsNullOrEmpty(acceptMediaType))
				request.Headers.Accept.Add(new(acceptMediaType));

			using HttpResponseMessage response = await client.SendAsync(request);

			response.EnsureSuccessStatusCode();

			string contentType = response.Content.Headers.ContentType?.MediaType;
			T result;

			if (contentType == MSGPACK_MEDIA_TYPE)
			{
				var stream = await response.Content.ReadAsStreamAsync();
				result = MessagePackSerializer.Deserialize<T>(stream);
			}
			else
			{
				result = await response.Content.ReadFromJsonAsync<T>();
			}

			return result;
		}

		static async Task Main(string[] args)
		{
			const string apiKey = "123123";
			const string apiHost = "https://admin.h-cms.net";
			const string pathMapperName = "demo";
			const int childrenFromPos = 0;
			const int takeChildren = 10;
			const bool siblings = true;
			const string ast = "ast=1&ast=2";

			Document doc;
			HttpClient client = new();

			while (true)
			{
				Console.Write("Enter document ID (or empty string to exit): ");
				string sId = Console.ReadLine();

				if (!int.TryParse(sId, out int id))
					break;

				string url = $"{apiHost}/api/v1/content/doc/{id}?pm={pathMapperName}&cfp={childrenFromPos}&tc={takeChildren}&sib={siblings}&{ast}";

				try
				{
					doc = await RestRequest<Document>(client, url, apiKey, MSGPACK_MEDIA_TYPE);

					string js = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

					Console.WriteLine(js);
					Console.WriteLine("--------------------------");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error: {ex.Message}");
				}
			}


			Console.WriteLine("Shutting down");
		}
	}
}
