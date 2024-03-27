﻿using System;
using System.Threading.Tasks;
using Arcus.BackgroundJobs.Tests.Integration.CloudEvents.Fixture;
using Arcus.BackgroundJobs.Tests.Integration.Fixture;
using Arcus.BackgroundJobs.Tests.Integration.Fixture.ServiceBus;
using Arcus.BackgroundJobs.Tests.Integration.Hosting;
using Arcus.Messaging.Abstractions.MessageHandling;
using Arcus.Messaging.Abstractions.ServiceBus.MessageHandling;
using Arcus.Messaging.Pumps.ServiceBus;
using Arcus.Testing.Logging;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Bogus;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using CloudEvent = Azure.Messaging.CloudEvent;

namespace Arcus.BackgroundJobs.Tests.Integration.CloudEvents
{
    [Trait("Category", "Integration")]
    [Collection(TestCollections.Integration)]
    public class CloudEventBackgroundJobTests
    {
        private const string TopicConnectionStringSecretKey = "Arcus:CloudEvents:ServiceBus:ConnectionStringWithTopic",
                             NamespaceConnectionStringSecretKey = "Arcus:CloudEvents:ServiceBus:NamespaceConnectionString",
                             TopicEndpointSecretKey = "Arcus:Infra:EventGrid:AuthKey";

        private readonly TestConfig _configuration;
        private readonly ILogger _logger;

        private static readonly Faker BogusGenerator = new Faker();

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudEventBackgroundJobTests" /> class.
        /// </summary>
        public CloudEventBackgroundJobTests(ITestOutputHelper outputWriter)
        {
            _configuration = TestConfig.Create();
            _logger = new XunitTestLogger(outputWriter);
        }

        private string TopicEndpoint => _configuration.GetRequiredValue<string>("Arcus:Infra:EventGrid:TopicUri");

        [Fact]
        public async Task CloudEventsBackgroundJobOnNamespace_ReceivesCloudEvents_ProcessesCorrectly()
        {
            // Arrange
            var options = new WorkerOptions();
            ConfigureCloudEventsBackgroundJobOnNamespace<CloudEventToEventGridAzureServiceBusMessageHandler, CloudEvent>(options)
                .ConfigureServices(services => services.AddAzureClients(clients => clients.AddEventGridPublisherClient(TopicEndpoint, TopicEndpointSecretKey)));

            CloudEvent expected = CreateCloudEvent();

            await using (var worker = await Worker.StartNewAsync(options))
            {
                TestServiceBusEventProducer producer = CreateEventProducer();
                await using (TestServiceBusEventConsumer consumer = await CreateEventConsumerAsync())
                {
                    // Act
                    await producer.ProduceAsync(expected);

                    // Assert
                    CloudEvent actual = consumer.Consume(expected.Id);
                    AssertCloudEvent(expected, actual);
                }
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobOnNamespaceUsingManagedIdentity_ReceivesCloudEvents_ProcessesCorrectly()
        {
            // Arrange
            ServicePrincipal servicePrincipal = _configuration.GetServicePrincipal();
            AzureEnvironment environment = _configuration.GetAzureEnvironment();
            using (TemporaryEnvironmentVariable.Create(EnvironmentVariables.AzureTenantId, environment.TenantId))
            using (TemporaryEnvironmentVariable.Create(EnvironmentVariables.AzureServicePrincipalClientId, servicePrincipal.ClientId))
            using (TemporaryEnvironmentVariable.Create(EnvironmentVariables.AzureServicePrincipalClientSecret, servicePrincipal.ClientSecret))
            {
                var options = new WorkerOptions();
                ConfigureCloudEventsBackgroundJobOnNamespaceUsingManagedIdentity<CloudEventToEventGridAzureServiceBusMessageHandler, CloudEvent>(options)
                    .ConfigureServices(services => services.AddAzureClients(clients => clients.AddEventGridPublisherClient(TopicEndpoint, TopicEndpointSecretKey)));

                CloudEvent expected = CreateCloudEvent();

                await using (var worker = await Worker.StartNewAsync(options))
                {
                    TestServiceBusEventProducer producer = CreateEventProducer();
                    await using (TestServiceBusEventConsumer consumer = await CreateEventConsumerAsync())
                    {
                        // Act
                        await producer.ProduceAsync(expected);

                        // Assert
                        CloudEvent actual = consumer.Consume(expected.Id);
                        AssertCloudEvent(expected, actual);
                    }
                }
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobOnNamespaceWithIgnoringMissingMembersDeserialization_ReceivesCloudEvents_MessageGetsProcessedByDifferentMessageHandler()
        {
            // Arrange
            var topicConnectionString = _configuration.GetValue<string>(TopicConnectionStringSecretKey);
            var properties = ServiceBusConnectionStringProperties.Parse(topicConnectionString);
            
            var options = new WorkerOptions();
            options.Configure(host =>
            {
                host.ConfigureAppConfiguration(context => context.AddConfiguration(_configuration))
                    .ConfigureSecretStore((config, stores) => stores.AddConfiguration(config));
            });
            options.ConfigureLogging(_logger)
                   .ConfigureServices(services =>
                   {
                       services.AddEventGridPublisher(_configuration);
                       services.AddCloudEventBackgroundJob(
                               topicName: properties.EntityPath,
                               subscriptionNamePrefix: "Test-",
                               serviceBusNamespaceConnectionStringSecretKey: NamespaceConnectionStringSecretKey,
                               opt => opt.Routing.Deserialization.AdditionalMembers = AdditionalMemberHandling.Ignore)
                           .WithServiceBusMessageHandler<OrdersV2AzureServiceBusMessageHandler, OrderV2>();
                   });

            var operationId = $"operation-{Guid.NewGuid()}";
            OrderV2 order = OrderGenerator.GenerateOrderV2();
            ServiceBusMessage message = 
                ServiceBusMessageBuilder.CreateForBody(order)
                                        .WithOperationId(operationId)
                                        .Build();

            await using (var worker = await Worker.StartNewAsync(options))
            {
                var producer = TestServiceBusEventProducer.Create(TopicConnectionStringSecretKey, _configuration);
                await using (var consumer = await TestServiceBusEventConsumer.StartNewAsync(_configuration, _logger))
                {
                    // Act
                    await producer.ProduceAsync(message);

                    // Assert
                    CloudEvent @event = consumer.Consume(operationId);
                    Assert.NotNull(@event.Data);

                    var orderCreatedEventData = @event.Data.ToObjectFromJson<OrderCreatedEventData>();
                    Assert.NotNull(orderCreatedEventData);
                    Assert.NotNull(orderCreatedEventData.CorrelationInfo);
                    Assert.Equal(order.Id, orderCreatedEventData.Id);
                    Assert.Equal(order.Amount, orderCreatedEventData.Amount);
                    Assert.Equal(order.ArticleNumber, orderCreatedEventData.ArticleNumber);
                    Assert.NotEmpty(orderCreatedEventData.CorrelationInfo.CycleId);
                }
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobOnNamespace_WithNoneTopicSubscription_DoesntCreateTopicSubscription()
        {
            // Arrange
            string topicConnectionString = _configuration.GetValue<string>(TopicConnectionStringSecretKey);
            var properties = ServiceBusConnectionStringProperties.Parse(topicConnectionString);
            
            var options = new WorkerOptions();
            string subscriptionPrefix = BogusGenerator.Name.Prefix();
            options.ConfigureServices(services => 
            {
                services.AddCloudEventBackgroundJob(
                            topicName: properties.EntityPath,
                            subscriptionNamePrefix: subscriptionPrefix,
                            serviceBusNamespaceConnectionStringSecretKey: NamespaceConnectionStringSecretKey,
                            opt => opt.TopicSubscription = TopicSubscription.None)
                        .WithServiceBusMessageHandler<OrdersAzureServiceBusMessageHandler, Order>();
            });

            // Act
            await using (var worker = await Worker.StartNewAsync(options))
            {
                var client = new ServiceBusAdministrationClient(topicConnectionString);

                SubscriptionProperties subscription = await GetTopicSubscriptionFromPrefix(client, subscriptionPrefix, properties.EntityPath);
                if (subscription != null)
                {
                    await client.DeleteSubscriptionAsync(properties.EntityPath, subscription.SubscriptionName);
                }

                Assert.Null(subscription);
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobOnTopic_ReceivesCloudEvents_ProcessesCorrectly()
        {
            // Arrange
            var options = new WorkerOptions();
            ConfigureCloudEventsBackgroundJobOnTopic<CloudEventToEventGridAzureServiceBusMessageHandler, CloudEvent>(options)
                .ConfigureServices(services => services.AddAzureClients(clients => clients.AddEventGridPublisherClient(TopicEndpoint, TopicEndpointSecretKey)));

            CloudEvent expected = CreateCloudEvent();

            await using (var worker = await Worker.StartNewAsync(options))
            {
                TestServiceBusEventProducer producer = CreateEventProducer();
                await using (TestServiceBusEventConsumer consumer = await CreateEventConsumerAsync())
                {
                    // Act
                    await producer.ProduceAsync(expected);

                    // Assert
                    CloudEvent actual = consumer.Consume(expected.Id);
                    AssertCloudEvent(expected, actual);
                }
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobWithIgnoringMissingMembersDeserialization_ReceivesCloudEvents_MessageGetsProcessedByDifferentMessageHandler()
        {
            // Arrange
            var options = new WorkerOptions();
            options.Configure(host =>
                   {
                       host.ConfigureAppConfiguration(context => context.AddConfiguration(_configuration))
                           .ConfigureSecretStore((config, stores) => stores.AddConfiguration(config));
                   }).ConfigureLogging(_logger)
                   .ConfigureServices(services =>
                   {
                       services.AddEventGridPublisher(_configuration);
                       services.AddCloudEventBackgroundJob(
                                   subscriptionNamePrefix: "Test-",
                                   serviceBusTopicConnectionStringSecretKey: TopicConnectionStringSecretKey,
                                   opt =>
                                   {
                                       opt.TopicSubscription = TopicSubscription.Automatic;
                                       opt.Routing.Deserialization.AdditionalMembers = AdditionalMemberHandling.Ignore;
                                   })
                               .WithServiceBusMessageHandler<OrdersV2AzureServiceBusMessageHandler, OrderV2>();
                   });

            var operationId = $"operation-{Guid.NewGuid()}";
            OrderV2 order = OrderGenerator.GenerateOrderV2();
            ServiceBusMessage message = 
                ServiceBusMessageBuilder.CreateForBody(order)
                                        .WithOperationId(operationId)
                                        .Build();

            // Act
            await using (var worker = await Worker.StartNewAsync(options))
            {
                var producer = TestServiceBusEventProducer.Create(TopicConnectionStringSecretKey, _configuration);
                await using (var consumer = await TestServiceBusEventConsumer.StartNewAsync(_configuration, _logger))
                {
                    // Act
                    await producer.ProduceAsync(message);

                    // Assert
                    CloudEvent @event = consumer.Consume(operationId);
                    Assert.NotNull(@event.Data);

                    var orderCreatedEventData = @event.Data.ToObjectFromJson<OrderCreatedEventData>();
                    Assert.NotNull(orderCreatedEventData);
                    Assert.NotNull(orderCreatedEventData.CorrelationInfo);
                    Assert.Equal(order.Id, orderCreatedEventData.Id);
                    Assert.Equal(order.Amount, orderCreatedEventData.Amount);
                    Assert.Equal(order.ArticleNumber, orderCreatedEventData.ArticleNumber);
                    Assert.NotEmpty(orderCreatedEventData.CorrelationInfo.CycleId);
                }
            }
        }

        [Fact]
        public async Task CloudEventsBackgroundJobOnTopic_WithNoneTopicSubscription_DoesntCreateTopicSubscription()
        {
            // Arrange
            string topicConnectionString = _configuration.GetValue<string>(TopicConnectionStringSecretKey);
            var properties = ServiceBusConnectionStringProperties.Parse(topicConnectionString);
            
            var options = new WorkerOptions();
            string subscriptionPrefix = BogusGenerator.Name.Prefix();
            options.ConfigureServices(services => 
            {
                services.AddEventGridPublisher(_configuration);
                services.AddCloudEventBackgroundJob(
                            subscriptionNamePrefix: subscriptionPrefix,
                            serviceBusTopicConnectionStringSecretKey: TopicConnectionStringSecretKey,
                            opt => opt.TopicSubscription = TopicSubscription.None)
                        .WithServiceBusMessageHandler<OrdersAzureServiceBusMessageHandler, Order>();
            });

            // Act
            await using (var worker = await Worker.StartNewAsync(options))
            {
                var client = new ServiceBusAdministrationClient(topicConnectionString);

                SubscriptionProperties subscription = await GetTopicSubscriptionFromPrefix(client, subscriptionPrefix, properties.EntityPath);
                if (subscription != null)
                {
                    await client.DeleteSubscriptionAsync(properties.EntityPath, subscription.SubscriptionName);
                }

                Assert.Null(subscription);
            }
        }

        private async Task<TestServiceBusEventConsumer> CreateEventConsumerAsync()
        {
            return await TestServiceBusEventConsumer.StartNewAsync(_configuration, _logger);
        }

        private TestServiceBusEventProducer CreateEventProducer()
        {
            return TestServiceBusEventProducer.Create(TopicConnectionStringSecretKey, _configuration);
        }

        private static void AssertCloudEvent(CloudEvent expected, CloudEvent actual)
        {
            Assert.Equal(expected.Id, actual.Id);

            var expectedData = expected.Data.ToObjectFromJson<StorageBlobCreatedEventData>();
            var actualData = actual.Data.ToObjectFromJson<StorageBlobCreatedEventData>();
            Assert.Equal(expectedData.Api, actualData.Api);
            Assert.Equal(expectedData.ClientRequestId, actualData.ClientRequestId);
        }

        private WorkerOptions ConfigureCloudEventsBackgroundJobOnTopic<TMessageHandler, TMessage>(WorkerOptions options) 
            where TMessageHandler : class, IAzureServiceBusMessageHandler<TMessage> 
            where TMessage : class
        {
            ConfigureSecretStoreWithConfiguration(options);
            options.ConfigureLogging(_logger)
                   .ConfigureServices(services =>
                   {
                       services.AddCloudEventBackgroundJob(
                                   subscriptionNamePrefix: "Test-",
                                   serviceBusTopicConnectionStringSecretKey: TopicConnectionStringSecretKey,
                                   opt => opt.TopicSubscription = TopicSubscription.Automatic)
                               .WithServiceBusMessageHandler<TMessageHandler, TMessage>();
                   });

            return options;
        }

        private WorkerOptions ConfigureCloudEventsBackgroundJobOnNamespaceUsingManagedIdentity<TMessageHandler, TMessage>(WorkerOptions options) 
            where TMessageHandler : class, IAzureServiceBusMessageHandler<TMessage> 
            where TMessage : class
        {
            var topicConnectionString = _configuration.GetValue<string>(TopicConnectionStringSecretKey);
            var properties = ServiceBusConnectionStringProperties.Parse(topicConnectionString);
            
            ConfigureSecretStoreWithConfiguration(options);
            options.ConfigureLogging(_logger)
                   .ConfigureServices(services =>
                   {
                       services.AddCloudEventBackgroundJobUsingManagedIdentity(
                                   topicName: properties.EntityPath, 
                                   subscriptionNamePrefix: "Test-", 
                                   serviceBusNamespace: properties.FullyQualifiedNamespace,
                                   configureBackgroundJob: opt => opt.TopicSubscription = TopicSubscription.Automatic)
                               .WithServiceBusMessageHandler<TMessageHandler, TMessage>();
                   });

            return options;
        }

        private WorkerOptions ConfigureCloudEventsBackgroundJobOnNamespace<TMessageHandler, TMessage>(WorkerOptions options) 
            where TMessageHandler : class, IAzureServiceBusMessageHandler<TMessage> 
            where TMessage : class
        {
            var topicConnectionString = _configuration.GetValue<string>(TopicConnectionStringSecretKey);
            var properties = ServiceBusConnectionStringProperties.Parse(topicConnectionString);
            
            ConfigureSecretStoreWithConfiguration(options);
            options.ConfigureLogging(_logger)
                   .ConfigureServices(services =>
                   {
                       services.AddCloudEventBackgroundJob(
                                   topicName: properties.EntityPath,
                                   subscriptionNamePrefix: "Test-",
                                   serviceBusNamespaceConnectionStringSecretKey: NamespaceConnectionStringSecretKey,
                                   opt => opt.TopicSubscription = TopicSubscription.Automatic)
                               .WithServiceBusMessageHandler<TMessageHandler, TMessage>();
                   });

            return options;
        }

        private void ConfigureSecretStoreWithConfiguration(WorkerOptions options)
        {
            options.Configure(host =>
            {
                host.ConfigureAppConfiguration(context => context.AddConfiguration(_configuration))
                    .ConfigureSecretStore((config, stores) => stores.AddConfiguration(config));
            });
        }

        private static CloudEvent CreateCloudEvent()
        {
            var data = new StorageBlobCreatedEventData(
                api: "PutBlockList",
                clientRequestId: "6d79dbfb-0e37-4fc4-981f-442c9ca65760",
                requestId: "831e1650-001e-001b-66ab-eeb76e000000",
                eTag: "0x8D4BCC2E4835CD0",
                contentType: "application/octet-stream",
                contentLength: 524288,
                blobType: "BlockBlob",
                url: "https://oc2d2817345i60006.blob.core.windows.net/oc2d2817345i200097container/oc2d2817345i20002296blob",
                sequencer: "00000000000004420000000000028963",
                storageDiagnostics: new
                {
                    batchId = "b68529f3-68cd-4744-baa4-3c0498ec19f0"
                });

            var cloudEvent = new CloudEvent(
                type: "Microsoft.Storage.BlobCreated",
                source: "/subscriptions/{subscription-id}/resourceGroups/{resource-group}/providers/Microsoft.Storage/storageAccounts/{storage-account}#blobServices/default/containers/{storage-container}/blobs/{new-file}",
                jsonSerializableData: data)
            {
                Id ="173d9985-401e-0075-2497-de268c06ff25", 
                Time = DateTimeOffset.UtcNow,
            };

            return cloudEvent;
        }

        private static async Task<SubscriptionProperties> GetTopicSubscriptionFromPrefix(ServiceBusAdministrationClient client, string subscriptionPrefix, string topicName)
        {
            AsyncPageable<SubscriptionProperties> subscriptionsResult = client.GetSubscriptionsAsync(topicName);
                
            await foreach (SubscriptionProperties subscriptionProperties in subscriptionsResult)
            {
                if (subscriptionProperties.SubscriptionName.StartsWith(subscriptionPrefix))
                {
                    return subscriptionProperties;
                }
            }

            return null;
        }
    }
}
