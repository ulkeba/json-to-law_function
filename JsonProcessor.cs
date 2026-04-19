using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text;
using System.Linq;
using System.Diagnostics;

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
        private static Lazy<string> lazyDataIngestorUamiClientId = new Lazy<string>(InitializeFromEnvSetting("DataIngestorUamiClientId", required: false));

        private static Lazy<string> lazyLogIngestionEndpoint = new Lazy<string>(InitializeFromEnvSetting("LOG_INGESTION_ENDPOINT"));
        private static Lazy<string> lazyMessageFormat = new Lazy<string>(InitializeFromEnvSetting("MESSAGE_FORMAT"));
        private static Lazy<HashSet<string>> lazyPrefixFilter = new Lazy<HashSet<string>>(InitializePrefixFilter());
        private static AccessToken? monitorToken = null;

        private static Lazy<int> lazyBatchSize = new Lazy<int>(() => 90);

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

        private static MonitorAuthenticationType? resolvedMonitorAuthType = null;

        public static MonitorAuthenticationType ValidateMonitorAuthenticationConfig(ILogger log)
        {
            var authTypeRaw = Environment.GetEnvironmentVariable("AZURE_MONITOR_AUTHENTICATION_TYPE");
            var hasTenant = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DataIngestorTenantId"));
            var hasClient = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DataIngestorClientId"));
            var hasSecret = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DataIngestorClientSecret"));
            var hasUamiClientId = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DataIngestorUamiClientId"));

            MonitorAuthenticationType authType;

            if (string.IsNullOrWhiteSpace(authTypeRaw))
            {
                // Backwards compatibility: infer auth type from legacy env vars
                if (hasTenant && hasClient && hasSecret)
                {
                    authType = MonitorAuthenticationType.RemoteServicePrincipalAndSecret;
                    log.LogInformation("[{logMessageType}] AZURE_MONITOR_AUTHENTICATION_TYPE is not set. " +
                        "Inferred {authType} from legacy DataIngestor env vars for backwards compatibility.",
                        LogMessageType.MonitorAuthenticationConfigValidation, authType);
                }
                else if (!hasTenant && !hasClient && !hasSecret)
                {
                    authType = MonitorAuthenticationType.Default;
                    log.LogInformation("[{logMessageType}] AZURE_MONITOR_AUTHENTICATION_TYPE is not set. " +
                        "No legacy DataIngestor env vars found, inferred {authType}.",
                        LogMessageType.MonitorAuthenticationConfigValidation, authType);
                }
                else
                {
                    throw LogAndCreateException(log,
                        "AZURE_MONITOR_AUTHENTICATION_TYPE is not set and the legacy DataIngestor env vars are partially configured. " +
                        "Either set all three (DataIngestorTenantId, DataIngestorClientId, DataIngestorClientSecret) for backwards compatibility, " +
                        "or set none and use AZURE_MONITOR_AUTHENTICATION_TYPE explicitly.");
                }
            }
            else
            {
                var normalized = authTypeRaw.Trim().ToUpperInvariant();
                authType = normalized switch
                {
                    "DEFAULT" => MonitorAuthenticationType.Default,
                    "REMOTE_SERVICE_PRINCIPAL_AND_SECRET" => MonitorAuthenticationType.RemoteServicePrincipalAndSecret,
                    "REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI" => MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughSami,
                    "REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI" => MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughUami,
                    _ => throw LogAndCreateException(log,
                        $"Unknown AZURE_MONITOR_AUTHENTICATION_TYPE value: '{authTypeRaw}'. " +
                        "Accepted values: DEFAULT, REMOTE_SERVICE_PRINCIPAL_AND_SECRET, " +
                        "REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI, REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI.")
                };
                log.LogInformation("[{logMessageType}] AZURE_MONITOR_AUTHENTICATION_TYPE explicitly set to {authType}.",
                    LogMessageType.MonitorAuthenticationConfigValidation, authType);
            }

            // Validate required env vars per auth type and reject unexpected ones
            switch (authType)
            {
                case MonitorAuthenticationType.Default:
                    if (hasTenant || hasClient || hasSecret || hasUamiClientId)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is DEFAULT but DataIngestor env vars are set. " +
                            "Remove DataIngestorTenantId, DataIngestorClientId, DataIngestorClientSecret, and DataIngestorUamiClientId " +
                            "or change the authentication type.");
                    break;

                case MonitorAuthenticationType.RemoteServicePrincipalAndSecret:
                    if (!hasTenant || !hasClient || !hasSecret)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_AND_SECRET but " +
                            "DataIngestorTenantId, DataIngestorClientId, and DataIngestorClientSecret must all be set.");
                    if (hasUamiClientId)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_AND_SECRET but " +
                            "DataIngestorUamiClientId is set. Remove it — it is only used with UAMI federation.");
                    break;

                case MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughSami:
                    if (!hasTenant || !hasClient)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI but " +
                            "DataIngestorTenantId and DataIngestorClientId must both be set.");
                    if (hasSecret)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI but " +
                            "DataIngestorClientSecret is set. Remove the secret — federation does not use a client secret.");
                    if (hasUamiClientId)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI but " +
                            "DataIngestorUamiClientId is set. Remove it — SAMI federation does not use a user-assigned managed identity.");
                    break;

                case MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughUami:
                    if (!hasTenant || !hasClient || !hasUamiClientId)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI but " +
                            "DataIngestorTenantId, DataIngestorClientId, and DataIngestorUamiClientId must all be set.");
                    if (hasSecret)
                        throw LogAndCreateException(log,
                            "AZURE_MONITOR_AUTHENTICATION_TYPE is REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI but " +
                            "DataIngestorClientSecret is set. Remove the secret — federation does not use a client secret.");
                    break;
            }

            resolvedMonitorAuthType = authType;
            log.LogInformation("[{logMessageType}] Azure Monitor authentication configuration validated successfully. Using {authType}.",
                LogMessageType.MonitorAuthenticationConfigValidation, authType);
            return authType;
        }

        private static InvalidOperationException LogAndCreateException(ILogger log, string message)
        {
            log.LogError("[{logMessageType}] {message}",
                LogMessageType.MonitorAuthenticationConfigValidation, message);
            return new InvalidOperationException(message);
        }

        private readonly ILogger<JsonProcessor> log;
        
        public JsonProcessor(ILogger<JsonProcessor> logger)
        {
            log = logger;
        }

        public enum MonitorAuthenticationType
        {
            Default,
            RemoteServicePrincipalAndSecret,
            RemoteServicePrincipalWithFederationThroughSami,
            RemoteServicePrincipalWithFederationThroughUami
        }

        enum LogMessageType
        {
            FunctionTriggered,
            BlobTypeAnalysis,
            SendBatchToMonitor,
            BlobProcessingCompleted,
            StorageAccountCredentials,
            AzureMonitorCredentials,
            SendingToAzureMonitor,
            SentAzureMonitor,
            FunctionExecutionCompleted,
            MonitorAuthenticationConfigValidation
        }

        enum BlobProcssingStrategy
        {
            PlainJson,
            JsonLinesInGZip,
            Skip
        }

        [Function("EventProcessor")]
        public void RunEventHubTrigger(
            [EventHubTrigger("storage-events", Connection = "EventHubConnectionAppSetting", ConsumerGroup = "to-function", IsBatched = false)] EventData eventData)
        {
            var EventPartitionId = eventData.PartitionKey ?? "unknown";
            var EventSequenceNumber = eventData.SequenceNumber;
            var EventOffset = "unknown"; //eventData.Offset;
            var EventEnqueuedTimeUtc = eventData.EnqueuedTime.DateTime;
            var TriggerInvokedTimeUtc = DateTime.UtcNow;
            var TimeSinceEnqueued = TriggerInvokedTimeUtc - EventEnqueuedTimeUtc;
            var EventPayload = Encoding.UTF8.GetString(eventData.EventBody.ToArray());

            log.LogInformation("[{logMessageType}] C# Function triggered for event from EventHub. " +
                "(message details: EventPartitionId: {EventPartitionId}, EventSequenceNumber: {EventSequenceNumber}, EventOffset: {EventOffset}, EventEnqueuedTimeUtc: {EventEnqueuedTimeUtc}, TriggerInvokedTimeUtc: {TriggerInvokedTimeUtc}, TimeSinceEnqueued: {TimeSinceEnqueued}, EventPayload: {EventPayload})",
                LogMessageType.FunctionTriggered, EventPartitionId, EventSequenceNumber, EventOffset, EventEnqueuedTimeUtc, TriggerInvokedTimeUtc, TimeSinceEnqueued, EventPayload);

            var jsonParsed = JsonNode.Parse(EventPayload);
            if (jsonParsed is JsonArray)
                foreach (var item in jsonParsed.AsArray())
                    ProcessEvent(item);
            else
                ProcessEvent(jsonParsed);

            log.LogInformation("[{logMessageType}] C# Function execution completed. " +
                "(message details: EventPartitionId: {EventPartitionId}, EventSequenceNumber: {EventSequenceNumber}, EventOffset: {EventOffset}, EventEnqueuedTimeUtc: {EventEnqueuedTimeUtc}, TriggerInvokedTimeUtc: {TriggerInvokedTimeUtc}, TimeSinceEnqueued: {TimeSinceEnqueued}, EventPayload: {EventPayload})",
                LogMessageType.FunctionExecutionCompleted, EventPartitionId, EventSequenceNumber, EventOffset, EventEnqueuedTimeUtc, TriggerInvokedTimeUtc, TimeSinceEnqueued, EventPayload);
        }

        private void ProcessEvent(JsonNode jsonParsed)
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

            if ("PutBlob".Equals(payload["api"].GetValue<string>())
                || "PutBlockList".Equals(payload["api"].GetValue<string>()))
            {
                
                string blobUrl = payload["url"].GetValue<string>();
                Uri blobUri = new Uri(blobUrl);

                string blobContentType = payload["contentType"].GetValue<string>();
                bool blobNameEndsWithJson = blobUrl.EndsWith(".json");
                bool blobNameEndsWithJsonGz = blobUrl.EndsWith(".json.gz");

                var filterIncludesBlob = lazyPrefixFilter.Value.Any(x => blobUri.AbsolutePath.StartsWith(x));

                BlobProcssingStrategy blobProcessingStrategy;
                if (filterIncludesBlob && blobNameEndsWithJsonGz && "application/octet-stream".Equals(blobContentType))
                    blobProcessingStrategy = BlobProcssingStrategy.JsonLinesInGZip;
                else if (filterIncludesBlob && blobNameEndsWithJson && "application/json".Equals(blobContentType))
                    blobProcessingStrategy = BlobProcssingStrategy.PlainJson;
                else
                    blobProcessingStrategy = BlobProcssingStrategy.Skip;


                log.LogInformation("[{logMessageType}] Strategy to process blob {blobUrl}: {blobProcessingStrategy} (filterIncludesBlob: {filterIncludesBlob}, blobContentType: {blobContentType}, blobNameEndsWithJson: {blobNameEndsWithJson}, blobNameEndsWithJsonGz: {blobNameEndsWithJsonGz})",
                    LogMessageType.BlobTypeAnalysis, blobUrl, blobProcessingStrategy, filterIncludesBlob, blobContentType, blobNameEndsWithJson, blobNameEndsWithJsonGz);

                if (blobProcessingStrategy == BlobProcssingStrategy.JsonLinesInGZip)
                {
                    var blobStream = GetBlobStream(blobUrl);
                    using var decompressor = new GZipStream(blobStream, CompressionMode.Decompress);
                    using var reader = new StreamReader(decompressor);

                    var line = reader.ReadLine();
                    var processedLines = 0;
                    var batch = 0;
                    StringBuilder myStringBuilder = new StringBuilder();
                    myStringBuilder.Append('[');
                    while (line != null)
                    {
                        if (myStringBuilder.Length > 1)
                            myStringBuilder.Append(", ");
                        myStringBuilder.Append(line);
                        processedLines++;

                        if (processedLines % lazyBatchSize.Value == 0)
                        {
                            log.LogInformation("[{logMessageType}] Processed {processedLines} lines from blob {blobUrl}. Sending batch {batch} to monitor...",
                                LogMessageType.SendBatchToMonitor, processedLines, blobUrl, batch);

                            myStringBuilder.Append(']');
                            StreamToMonitor(new StringContent(myStringBuilder.ToString(), System.Text.Encoding.UTF8, "application/json"));

                            batch++;
                            myStringBuilder = new StringBuilder();
                            myStringBuilder.Append('[');
                        }

                        line = reader.ReadLine();
                    }
                    log.LogInformation("[{logMessageType}] Processed {processedLines} lines from blob {blobUrl}. Sending final batch {batch} to monitor...",
                        LogMessageType.SendBatchToMonitor, processedLines, blobUrl, batch);

                    myStringBuilder.Append(']');
                    StreamToMonitor(new StringContent(myStringBuilder.ToString(), System.Text.Encoding.UTF8, "application/json"));

                    log.LogInformation("[{logMessageType}] Finished processing blob {blobUrl} with strategy {blobProcessingStrategy}. Total processed lines: {processedLines}, Total batches sent: {batch}.",
                        LogMessageType.BlobProcessingCompleted, blobUrl, blobProcessingStrategy, processedLines, batch);
                }
                
                if (blobProcessingStrategy == BlobProcssingStrategy.PlainJson)
                {
                    var blobStream = GetBlobStream(blobUrl);
                    string blobContent = GetContentFromStream(blobStream);
                    StreamToMonitor(new StringContent(blobContent, System.Text.Encoding.UTF8, "application/json"));
                    log.LogInformation("[{logMessageType}] Finished processing blob {blobUrl} with strategy {blobProcessingStrategy}.",
                        LogMessageType.BlobProcessingCompleted, blobUrl, blobProcessingStrategy);
                }
            }
        }

        private Stream GetBlobStream(string blobUrl)
        {
            try
            {
                TokenCredential credential;
                if (!string.IsNullOrEmpty(lazyDataFetcherTenantId.Value)
                    && !string.IsNullOrEmpty(lazyDataFetcherClientId.Value)
                    && !string.IsNullOrEmpty(lazyDataFetcherClientSecret.Value))
                {
                    log.LogInformation("[{logMessageType}] Using ClientSecretCredential(.) with Tenant ID {tenantId}, Client ID {clientId} and Client Secret to get new token for Storage Account...",
                        LogMessageType.StorageAccountCredentials, lazyDataFetcherTenantId.Value, lazyDataFetcherClientId.Value);
                    credential = new ClientSecretCredential(lazyDataFetcherTenantId.Value, lazyDataFetcherClientId.Value, lazyDataFetcherClientSecret.Value);
                }
                else
                {
                    log.LogInformation("[{logMessageType}] Using DefaultAzureCredential(.) to get new token for Storage Account...",
                        LogMessageType.StorageAccountCredentials);
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

        private string GetContentFromStream(Stream stream)
        {
            // TODO Error handling in case of different encoding. 
            StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string text = reader.ReadToEnd();
            return text;
        }

        private void StreamToMonitor(HttpContent content)
        {
            try
            {
                string accessToken = GetMonitorToken();
                HttpClient monitorHttpClient = new HttpClient();
                monitorHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                log.LogInformation("[{logMessageType}] Sending payload to {endpoint}...",
                    LogMessageType.SendingToAzureMonitor, lazyLogIngestionEndpoint.Value);
                var response = monitorHttpClient.PostAsync(lazyLogIngestionEndpoint.Value, content);
                log.LogInformation("[{logMessageType}] Result code is {resultCode}...",
                    LogMessageType.SendingToAzureMonitor, response.Result.StatusCode);
                response.Result.EnsureSuccessStatusCode();

            }
            catch (Exception ex)
            {
                log.LogError(ex.ToString());
                throw;
            }
        }

        private string GetMonitorToken()
        {
            if (!monitorToken.HasValue || monitorToken.Value.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
            {
                TokenCredential credential;
                switch (resolvedMonitorAuthType)
                {
                    case MonitorAuthenticationType.Default:
                        log.LogInformation("[{logMessageType}] Using DefaultAzureCredential(.) to get new token for Azure Monitor...",
                            LogMessageType.AzureMonitorCredentials);
                        credential = new DefaultAzureCredential();
                        break;

                    case MonitorAuthenticationType.RemoteServicePrincipalAndSecret:
                        log.LogInformation("[{logMessageType}] Using ClientSecretCredential(.) with Tenant ID {tenantId}, Client ID {clientId} and Client Secret to get new token for Azure Monitor...",
                            LogMessageType.AzureMonitorCredentials, lazyDataIngestorTenantId.Value, lazyDataIngestorClientId.Value);
                        credential = new ClientSecretCredential(lazyDataIngestorTenantId.Value, lazyDataIngestorClientId.Value, lazyDataIngestorClientSecret.Value);
                        break;

                    case MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughSami:
                        log.LogError("[{logMessageType}] Federation through SAMI is not yet implemented.",
                            LogMessageType.AzureMonitorCredentials);
                        throw new NotImplementedException(
                            "Azure Monitor authentication via REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI is not yet implemented.");

                    case MonitorAuthenticationType.RemoteServicePrincipalWithFederationThroughUami:
                        log.LogError("[{logMessageType}] Federation through UAMI is not yet implemented.",
                            LogMessageType.AzureMonitorCredentials);
                        throw new NotImplementedException(
                            "Azure Monitor authentication via REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI is not yet implemented.");

                    default:
                        throw new InvalidOperationException(
                            $"Unexpected MonitorAuthenticationType: {resolvedMonitorAuthType}. " +
                            "Was ValidateMonitorAuthenticationConfig() called at startup?");
                }

                CancellationToken cancellationToken = new CancellationToken();
                monitorToken = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://monitor.azure.com//.default" }), cancellationToken);
            }
            else
            {
                log.LogInformation("[{logMessageType}] Using cached token for Azure Monitor...",
                    LogMessageType.AzureMonitorCredentials);
            }
            return monitorToken.Value.Token;
        }

    }
}

