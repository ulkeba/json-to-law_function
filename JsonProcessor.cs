using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text;
using System.Linq;

namespace JsonToSentinelFunction
{
    public class JsonProcessor
    {
        private static Lazy<string> lazyDataFetcherTenantId = new Lazy<string>(InitializeFromEnvSetting("DataFetcherTenantId", required: false));
        private static Lazy<string> lazyDataFetcherClientId = new Lazy<string>(InitializeFromEnvSetting("DataFetcherClientId", required: false));
        private static Lazy<string> lazyDataFetcherClientSecret = new Lazy<string>(InitializeFromEnvSetting("DataFetcherClientSecret", required: false));

        private static Lazy<string> lazyDataIngestorTenantId = new Lazy<string>(InitializeFromEnvSetting("DataIngestorTenantId", required: false));
        private static Lazy<string> lazyDataIngestorClientId = new Lazy<string>(InitializeFromEnvSetting("DataIngestorClientId", required: false));
        private static Lazy<string> lazyDataIngestorClientSecret = new Lazy<string>(InitializeFromEnvSetting("DataIngestorClientSecret", required: false));

        private static Lazy<string> lazyLogIngestionEndpoint = new Lazy<string>(InitializeFromEnvSetting("LOG_INGESTION_ENDPOINT"));
        private static Lazy<string> lazyMessageFormat = new Lazy<string>(InitializeFromEnvSetting("MESSAGE_FORMAT"));
        private static Lazy<string> lazyTransmissionMode = new Lazy<string>(InitializeFromEnvSetting("TRANSMISSION_MODE", required: false));
        private static Lazy<HashSet<string>> lazyPrefixFilter = new Lazy<HashSet<string>>(InitializePrefixFilter());
        private static AccessToken? monitorToken = null;

        private static string InitializeFromEnvSetting(string key, bool required = true)
        {
            string retVal = Environment.GetEnvironmentVariable(key);
            if (required && retVal == null)
                throw new Exception($"{key} must be specified.");
            return retVal;
        }

        private static HashSet<string> InitializePrefixFilter()
        {
            var retVal = new HashSet<string>();
            var prefixFilterString = InitializeFromEnvSetting("PREFIX_FILTER");
            prefixFilterString.Split(",").ToList().ForEach(x => retVal.Add(x.Trim()));
            return retVal;
        }

        [FunctionName("EventProcessor")]
        public void RunEventGridTrigger(
            [EventHubTrigger("storage-events", Connection = "EventHubConnectionAppSetting", ConsumerGroup = "to-function")] string eventHubMessage,
            ILogger log)
        {
            var data = eventHubMessage;
            log.LogInformation($"C# Event hub trigger function Processed event :{data}");

            var jsonParsed = JsonNode.Parse(data);
            if (jsonParsed is JsonArray)
                foreach (var item in jsonParsed.AsArray())
                    ProcessEvent(item, log);
            else
                ProcessEvent(jsonParsed, log);

        }

        private void ProcessEvent(JsonNode jsonParsed, ILogger log)
        {
            JsonNode payload;
            if ("EventGridSchema_in_EventHub".Equals(lazyMessageFormat.Value))
            {
                payload = jsonParsed["data"];
            }
            else if ("EventGridSchema".Equals(lazyMessageFormat.Value))
            {
                payload = jsonParsed;
            }
            else
            {
                throw new Exception($"Unknown message format {lazyMessageFormat.Value}");
            }

            if ("PutBlob".Equals(payload["api"].GetValue<string>()))
            {
                string blobUrl = payload["url"].GetValue<string>();
                Uri blobUri = new Uri(blobUrl);

                var filtered = lazyPrefixFilter.Value.Any(x => blobUri.AbsolutePath.StartsWith(x));
                if (filtered)
                {
                    if ("application/octet-stream".Equals(payload["contentType"].GetValue<string>()))
                    {
                        log.LogInformation($"Blob {blobUrl} is of type application/octet-stream. Checking for file extension...");
                        if (blobUrl.EndsWith(".json.gz"))
                        {
                            log.LogInformation($"Blob {blobUrl} is of type application/octet-stream and has a .json.gz extension. Extracting (and assuming it's in JSONLines format)...");
                            var blobStream = GetBlobStream(blobUrl, log);
                            using var decompressor = new GZipStream(blobStream, CompressionMode.Decompress);
                            using var reader = new StreamReader(decompressor);

                            var line = reader.ReadLine();
                            StringBuilder myStringBuilder = new StringBuilder();
                            myStringBuilder.Append('[');
                            while (line != null)
                            {
                                if (myStringBuilder.Length > 1)
                                    myStringBuilder.Append(", ");
                                myStringBuilder.Append(line);
                                line = reader.ReadLine();
                            }
                            myStringBuilder.Append(']');
                            StreamToMonitor(new StringContent(myStringBuilder.ToString(), System.Text.Encoding.UTF8, "application/json"), log);
                        }
                        else
                        {
                            log.LogInformation($"Blob {blobUrl} is of type application/octet-stream but does not have a .json.gz extension. Skipping...");
                        }
                    }
                    else if ("application/json".Equals(payload["contentType"].GetValue<string>()))
                    {
                        log.LogInformation($"Blob {blobUrl} is of type application/json. Checking for file extension...");
                        if (blobUrl.EndsWith(".json"))
                        {
                            log.LogInformation($"Blob {blobUrl} is of type application/json and has a .json extension. Processing...");
                            var blobStream = GetBlobStream(blobUrl, log);
                            if (!string.IsNullOrEmpty(lazyTransmissionMode.Value) && "read_full".Equals(lazyTransmissionMode.Value.ToLower()))
                            {
                                log.LogInformation($"TRANSMISSION_MODE is set to read_full. Reading full content of blob {blobUrl}...");
                                string blobContent = GetContentFromStream(blobStream, log);
                                log.LogInformation($"Read blob {blobUrl}; content is: {blobContent}");
                                StreamToMonitor(new StringContent(blobContent, System.Text.Encoding.UTF8, "application/json"), log);
                            }
                            else
                            {
                                log.LogInformation($"TRANSMISSION_MODE is not set to read_full. Streaming content of {blobUrl} directly...");
                                StreamToMonitor(new StreamContent(blobStream), log);
                            }

                        }
                        else
                        {
                            log.LogInformation($"Blob {blobUrl} is of type application/json but does not have a .json extension. Skipping...");
                        }

                    }
                    else
                    {
                        log.LogInformation($"Blob {blobUrl} is of type {payload["contentType"].GetValue<string>()}. Skipping...");
                    }
                }
                else
                {
                    log.LogInformation($"Blob {blobUrl} does not match prefix filter {string.Join(',', lazyPrefixFilter.Value)}. Skipping...");
                }
            }
        }

        private Stream GetBlobStream(string blobUrl, ILogger log)
        {
            try
            {
                TokenCredential credential;
                if (!string.IsNullOrEmpty(lazyDataFetcherTenantId.Value)
                    && !string.IsNullOrEmpty(lazyDataFetcherClientId.Value)
                    && !string.IsNullOrEmpty(lazyDataFetcherClientSecret.Value))
                {
                    log.LogInformation($"Using ClientSecretCredential(.) with Tenant ID {lazyDataFetcherTenantId.Value}, Client ID {lazyDataFetcherClientId.Value} and Client Secret to get new token for Storage Account...");
                    credential = new ClientSecretCredential(lazyDataFetcherTenantId.Value, lazyDataFetcherClientId.Value, lazyDataFetcherClientSecret.Value);
                }
                else
                {
                    log.LogInformation($"Using DefaultAzureCredential(.) to get new token for Storage Account...");
                    credential = new Azure.Identity.DefaultAzureCredential();
                }

                BlobClient blobClient = new(new Uri(blobUrl), credential);
                return blobClient.OpenRead();
            }
            catch (Exception ex)
            {
                log.LogError(ex.ToString());
                throw;
            }
        }

        private string GetContentFromStream(Stream stream, ILogger log)
        {
            // TODO Error handling in case of different encoding. 
            StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string text = reader.ReadToEnd();
            return text;
        }

        private void StreamToMonitor(HttpContent content, ILogger log)
        {
            try
            {
                string accessToken = GetMonitorToken(log);
                HttpClient monitorHttpClient = new HttpClient();
                monitorHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                log.LogInformation($"Sending payload to {lazyLogIngestionEndpoint.Value}...");
                var response = monitorHttpClient.PostAsync(lazyLogIngestionEndpoint.Value, content);
                log.LogInformation($"Result code is {response.Result.EnsureSuccessStatusCode()}...");
                response.Result.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                log.LogError(ex.ToString());
                throw;
            }
        }

        private string GetMonitorToken(ILogger log)
        {
            if (!monitorToken.HasValue || monitorToken.Value.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
            {
                TokenCredential credential;
                if (!string.IsNullOrEmpty(lazyDataIngestorClientSecret.Value)
                    && !string.IsNullOrEmpty(lazyDataIngestorClientId.Value)
                    && !string.IsNullOrEmpty(lazyDataIngestorTenantId.Value))
                {
                    log.LogInformation($"Using ClientSecretCredential(.) with Tenant ID {lazyDataIngestorTenantId.Value}, Client ID {lazyDataIngestorClientId.Value} and Client Secret to get new token for Azure Monitor...");
                    credential = new ClientSecretCredential(lazyDataIngestorTenantId.Value, lazyDataIngestorClientId.Value, lazyDataIngestorClientSecret.Value);
                }
                else
                {
                    log.LogInformation($"Using DefaultAzureCredential(.) to get new token for Azure Monitor...");
                    credential = new Azure.Identity.DefaultAzureCredential();
                }
                CancellationToken cancellationToken = new CancellationToken();
                monitorToken = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://monitor.azure.com//.default" }), cancellationToken);
            }
            else
            {
                log.LogInformation($"Using cached token for Azure Monitor...");
            }
            return monitorToken.Value.Token;
        }

    }
}
