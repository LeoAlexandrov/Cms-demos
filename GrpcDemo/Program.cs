using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

using Grpc.Core;
using Grpc.Net.Client;


using HCms.Content;
using HCms.Content.ViewModels;


namespace GrpcDemo
{
	internal class Program
	{

		static async Task Main(string[] args)
		{
			const string apiKey = "123123";

			var credentials = CallCredentials.FromInterceptor((context, metadata) =>
			{
				metadata.Add("APIKey", apiKey);
				return Task.CompletedTask;
			});

			var channelOptions = new GrpcChannelOptions()
			{
				Credentials = ChannelCredentials.Create(new SslCredentials(), credentials)
			};

			// specifying this address assumes that CMS is already running locally from VS
			const string address = "https://localhost:7284";

			using var channel = GrpcChannel.ForAddress(address, channelOptions);

			var client = new ContentGrpcService.ContentGrpcServiceClient(channel);
			int[] allowedStatus = [1, 2];

			while (true)
			{
				Console.Write("Enter document ID (or empty string to exit): ");
				string sId = Console.ReadLine();
				
				if (!int.TryParse(sId, out int id))
					break;

				var request = new DocumentGrpcRequest() { Id = id, PathMapper = "demo" };
				request.AllowedStatus.Add(allowedStatus);

				var reply = await client.GetDocumentAsync(request);
				var doc = MessagePack.MessagePackSerializer.Deserialize<Document>(reply.Data.ToArray());
				string js = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

				Console.WriteLine(js);
				Console.WriteLine("--------------------------");
			}


			Console.WriteLine("Shutting down");
		}
	}
}
