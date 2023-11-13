using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Azure.Messaging.EventHubs;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonToSentinelFunction
{
    public class JsonProcessor
    {
        private static Lazy<string> lazyLogIngestionEndpoint = new Lazy<string>(InitializeLogIngestionEndpoint);

        private static string InitializeLogIngestionEndpoint()
        {
            string retVal = Environment.GetEnvironmentVariable("LOG_INGESTION_ENDPOINT");
            if (retVal == null)
                throw new Exception("LOG_INGESTION_ENDPOINT must be specified.");
            return retVal;
        }

        [FunctionName("EventProcessor")]
        public void RunEventGridTrigger(
            [EventHubTrigger("storage-events", Connection = "EventHubConnectionAppSetting", ConsumerGroup = "to-function")] string eventHubMessage,
            ILogger log)
        {
            var data = eventHubMessage;
            log.LogInformation($"C# Event hub trigger function Processed event :{data}");

            //TODO: More careful filtering (see https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-event-overview).
            var jsonParsed = JsonNode.Parse(data);
            if ("PutBlob".Equals(jsonParsed["api"].GetValue<string>())) {
                string blobUrl = jsonParsed["url"].GetValue<string>();
                string blobContent = GetBlobContent(blobUrl, log);
                log.LogInformation($"Read blob {blobUrl}; content is: {blobContent}");

                String accessToken = GetMonitorToken();
                PostToMonitor(lazyLogIngestionEndpoint.Value, accessToken, blobContent, log);
            }
        }

        public string GetBlobContent(string blobUrl, ILogger log)
        {
            try
            {
                BlobClient client = new(
                    new Uri(blobUrl),
                    new DefaultAzureCredential());
                var v = client.DownloadContent();

                var binaryStream = v.Value.Content.ToStream();
                // TODO Error handling in case of different encoding. 
                StreamReader reader = new StreamReader(binaryStream, System.Text.Encoding.UTF8);
                string text = reader.ReadToEnd();
                return text;
            }
            catch (Exception ex)
            {
                log.LogError(ex.ToString());
                throw;
            }
        }

        //TODO Cache the token and renew on demand.
        private string GetMonitorToken() {
            var credential = new Azure.Identity.DefaultAzureCredential();
            var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://monitor.azure.com//.default" }));
            return token.Token;
        }

        private void PostToMonitor(string ingestionEndpoint, string accessToken, string payload, ILogger log)
        {
            //TODO Proper Exception handling / retrying / dead-lettering.
            try
            {
                log.LogInformation($"Sending payload to {ingestionEndpoint}...");
                HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var response = client.PostAsync(ingestionEndpoint, content);
                response.Result.EnsureSuccessStatusCode().ToString();
            }
            catch (Exception ex)
            {
                log.LogError(ex.ToString());
                throw;
            }
        }
    }
}
