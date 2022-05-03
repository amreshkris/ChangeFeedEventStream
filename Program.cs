using System.Net.WebSockets;
using System.Net.Http;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Net.Http.Json;

namespace ChangeFeedEventStream
{
    class Program
    {
        private static readonly string monitoredContainer = "cart-container";
        //private static readonly string leasesContainer = "leases-cart-container";
        //private static readonly string partitionKeyPath = "/id";
        private static readonly string databaseId = "changefeed-basic";

        static async Task Main(string[] args)
        {
            try
            {
                IConfigurationRoot configuration = new ConfigurationBuilder()
                  .AddJsonFile("appSettings.json")
                  .Build();

                InitializeSettings(configuration, out string endpoint, out string authKey);

                using (CosmosClient client = new CosmosClient(endpoint, authKey))
                {
                    Container monitoredContainer = client.GetContainer(databaseId, Program.monitoredContainer);
                    var iterator = monitoredContainer.GetChangeFeedIterator<Cart>(ChangeFeedStartFrom.Time(DateTime.MinValue.ToUniversalTime()), ChangeFeedMode.Incremental);
                    
                    while (iterator.HasMoreResults)
                    {
                        try
                        {
                            // Read data from Change Feed
                            var response = await iterator.ReadNextAsync();

                            if (response.StatusCode == HttpStatusCode.NotModified)
                            {
                                Console.WriteLine($"No new changes");
                                await Task.Delay(TimeSpan.FromSeconds(10));
                            }
                            foreach (var cart in response)
                            {
                                PublishEventToStream(cart, configuration);
                            }
                        }
                        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotModified)
                        {
                            // If Change Feed is empty, an exception will be thrown with StatusCode set to NotModified
                            await Task.Delay(5000);
                        }

                    }
                    Console.ReadKey();
                }
            }
            catch
            {
                throw;
            }
        }

        private static void InitializeSettings(IConfigurationRoot configuration, out string endpoint, out string authKey)
        {
            endpoint = configuration["EndPointUrl"];
            if (string.IsNullOrEmpty(endpoint))
            {
                throw new ArgumentNullException
                ("Please specify a valid endpoint in the appSettings.json");
            }



            authKey = configuration["AuthorizationKey"];
            if (string.IsNullOrEmpty(authKey) || string.Equals(authKey, "Super secret key"))
            {
                throw new ArgumentException("Please specify a valid AuthorizationKey in the appSettings.json");
            }
        }

        private static async void PublishEventToStream(Cart cart, IConfigurationRoot configuration)
        {
            var eventHubConnection = configuration["EventHubConnection"];
            if (string.IsNullOrEmpty(eventHubConnection))
            {
                throw new ArgumentException("Please specify a valid EventHubConnection in the appSettings.json");
            }

            var eventHubName = configuration["EventHubName"];
            if (string.IsNullOrEmpty(eventHubName))
            {
                throw new ArgumentException("Please specify a valid EventHubName in the appSettings.json");
            }

            await using (var producer = new EventHubProducerClient(eventHubConnection, eventHubName))
            {
                using EventDataBatch eventBatch = await producer.CreateBatchAsync();
                eventBatch.TryAdd(new EventData(new BinaryData(cart)));
                await producer.SendAsync(eventBatch);
            }

            //Publish to PowerBI Streaming Source API
            var analyticsURI = configuration["AnalyticsURI"];
            if (string.IsNullOrEmpty(analyticsURI))
            {
                throw new ArgumentException("Please specify a valid analyticsURI in the appSettings.json");
            }

            HttpClient client = new HttpClient();
            //client.DefaultRequestHeaders.Add("Content-Type","application/json");
            List<StreamCartData> streamData = new List<StreamCartData>();
            streamData.Add(new StreamCartData(){OrderStatus = cart.OrderStatus.ToString(),Total = cart.Total});
            var deserializedStreamData = System.Text.Json.JsonSerializer.Serialize(streamData);
            Console.WriteLine($"deserializedStreamData is {deserializedStreamData}");
            var response = client.PostAsJsonAsync(analyticsURI, streamData).Result;
            if (response.IsSuccessStatusCode)
            {
                Console.Write("published cart data");
            }
            else
            {
                Console.Write("Error in publishin cart data");
            }

        }
    }
}