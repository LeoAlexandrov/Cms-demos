using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

using AleProjects.Endpoints;
using HCms.Dto;


namespace DemoSite.Services
{

	/// <summary>
	/// Provides a hosted service for subscribing to events from Redis Pub/Sub and RabbitMQ sent by CMS
	/// notifying consumers about content changes. Used for cache updates.
	/// </summary>
	public class EventSubscriptionService(
		IConfiguration configuration, 
		IServiceScopeFactory serviceScopeFactory,
		ILogger<EventSubscriptionService> logger) : IDisposable, IHostedService
	{
		readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
		readonly ILogger<EventSubscriptionService> _logger = logger;

		readonly string redisEndpoints = configuration["Redis:Endpoints"];
		readonly string redisUser = configuration["Redis:User"];
		readonly string redisPassword = configuration["Redis:Password"];
		readonly RedisChannel redisChannel = RedisChannel.Literal(configuration["Redis:Channel"]);
		
		readonly string rabbitHost = configuration["Rabbit:Host"];
		readonly string rabbitUser = configuration["Rabbit:User"];
		readonly string rabbitPassword = configuration["Rabbit:Password"];
		readonly string rabbitExchange = configuration["Rabbit:Exchange"];
		readonly string rabbitExchangeType = configuration["Rabbit:ExchangeType"];
		readonly string rabbitRoutingKey = configuration["Rabbit:RoutingKey"];

		ConnectionMultiplexer redisConnection;
		ISubscriber redisSubscriber;

		IConnection rabbitConnection;
		IChannel rabbitChannel;
		AsyncEventingBasicConsumer rabbitConsumer;


		async Task SubscribeRedis(CancellationToken cancellationToken)
		{
			ConfigurationOptions opts = new()
			{
				EndPoints = [.. EndPoints.Parse(redisEndpoints)],
			};

			if (!string.IsNullOrEmpty(redisUser))
			{
				opts.User = redisUser;

				if (!string.IsNullOrEmpty(redisPassword))
					opts.Password = redisPassword;
			}

			redisConnection = await ConnectionMultiplexer.ConnectAsync(opts);
			redisSubscriber = redisConnection.GetSubscriber();

			await redisSubscriber.SubscribeAsync(redisChannel, RedisEventHandler);

			_logger.LogInformation("Subscribed to Redis Pub/Sub.");
		}

		async Task SubscribeRabbit(CancellationToken cancellationToken)
		{
			string host;
			int port;

			if (EndPoints.TryParse(rabbitHost, out EndPoint endpoint))
			{
				if (endpoint is IPEndPoint ipEndPoint)
				{
					host = ipEndPoint.Address.ToString();
					port = ipEndPoint.Port;
				}
				else if (endpoint is DnsEndPoint dnsEndPoint)
				{
					host = dnsEndPoint.Host;
					port = dnsEndPoint.Port;
				}
				else
				{
					host = rabbitHost;
					port = AmqpTcpEndpoint.UseDefaultPort;
				}
			}
			else
			{
				host = rabbitHost;
				port = AmqpTcpEndpoint.UseDefaultPort;
			}


			var factory = new ConnectionFactory { HostName = host, Port = port };

			if (!string.IsNullOrEmpty(rabbitUser))
			{
				factory.UserName = rabbitUser;

				if (!string.IsNullOrEmpty(rabbitPassword))
					factory.Password = rabbitPassword;
			}

			rabbitConnection = await factory.CreateConnectionAsync(cancellationToken);
			rabbitChannel = await rabbitConnection.CreateChannelAsync(cancellationToken: cancellationToken);

			await rabbitChannel.ExchangeDeclareAsync(exchange: rabbitExchange, 
				type: rabbitExchangeType ?? ExchangeType.Fanout, 
				cancellationToken: cancellationToken);

			QueueDeclareOk queueDeclareResult = await rabbitChannel.QueueDeclareAsync(cancellationToken: cancellationToken);
			string queueName = queueDeclareResult.QueueName;

			await rabbitChannel.QueueBindAsync(queue: queueName, 
				exchange: rabbitExchange, 
				routingKey: rabbitRoutingKey ?? string.Empty,
				cancellationToken: cancellationToken);

			rabbitConsumer = new AsyncEventingBasicConsumer(rabbitChannel);

			rabbitConsumer.ReceivedAsync += RabbitEventHandler;

			await rabbitChannel.BasicConsumeAsync(queueName, autoAck: true, consumer: rabbitConsumer);

			_logger.LogInformation("Subscribed to RabbitMQ.");
		}


		public async Task StartAsync(CancellationToken cancellationToken)
		{
			if (!string.IsNullOrEmpty(redisEndpoints))
				await SubscribeRedis(cancellationToken);
			
			if (!string.IsNullOrEmpty(rabbitHost))
				await SubscribeRabbit(cancellationToken);

			_logger.LogInformation("Event subscription service has been started.");
		}

		public void RedisEventHandler(RedisChannel channel, RedisValue message)
		{
			using (var scope = _serviceScopeFactory.CreateScope())
			{
				var payload = System.Text.Json.JsonSerializer.Deserialize<EventPayload>(message);

				var cmsService = scope.ServiceProvider.GetService<CmsContentService>();
				cmsService?.UpdateCache(payload);

				var s3Service = scope.ServiceProvider.GetService<S3MediaStorage>();
				s3Service?.UpdateCache(payload);
			}

			_logger.LogInformation("Message received from Redis Pub/Sub.");
		}

		public Task RabbitEventHandler(object sender, BasicDeliverEventArgs ea)
		{
			using (var scope = _serviceScopeFactory.CreateScope())
			{
				byte[] body = ea.Body.ToArray();
				var message = Encoding.UTF8.GetString(body);
				var payload = System.Text.Json.JsonSerializer.Deserialize<EventPayload>(message);

				var cmsService = scope.ServiceProvider.GetService<CmsContentService>();
				cmsService?.UpdateCache(payload);

				var s3Service = scope.ServiceProvider.GetService<S3MediaStorage>();
				s3Service?.UpdateCache(payload);
			}

			_logger.LogInformation("Message received from RabbitMQ.");

			return Task.CompletedTask;
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			redisSubscriber?.Unsubscribe(redisChannel);

			if (rabbitChannel != null)
			{
				if (rabbitConsumer != null)
					rabbitConsumer.ReceivedAsync -= RabbitEventHandler;

				await rabbitConnection.CloseAsync(cancellationToken);
			}

			_logger.LogInformation("Event subscription service has been stopped.");
		}

		public void Dispose()
		{
			redisConnection?.Dispose();
		}

	}

}