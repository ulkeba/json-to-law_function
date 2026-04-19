# Copilot Instructions

## Build

```bash
dotnet build
dotnet run          # starts the Azure Functions host on port 7261
```

There are no tests or lint tooling configured in this project.

## Architecture

This is an **Azure Functions v4 Isolated Worker** (.NET 8) that ingests log data from Azure Blob Storage and forwards it to **Azure Monitor / Microsoft Sentinel** via the Log Ingestion API.

**Data flow:**

1. An **Event Hub trigger** (`EventProcessor` function) receives storage events (blob created).
2. The function determines a processing strategy based on blob content type and path prefix:
   - `.json.gz` blobs → decompress gzip, read JSON Lines, batch into groups of 90, POST each batch.
   - `.json` blobs → read as plain JSON, POST directly.
   - Everything else → skip.
3. Blob content is fetched from **Azure Blob Storage** using either `ClientSecretCredential` or `DefaultAzureCredential`.
4. Batches are POSTed to the **Azure Monitor Log Ingestion endpoint** with a Bearer token (cached with 5-minute refresh margin).

**Key components:**

- `Program.cs` — Host bootstrap with Application Insights telemetry.
- `JsonProcessor.cs` — All business logic: event parsing, blob fetching, batching, and Monitor ingestion. This is the only substantial file.

## Conventions

- **Configuration via environment variables** — all settings are read from env vars using `Lazy<T>` initialization with an `InitializeFromEnvSetting` helper. Required env vars: `LOG_INGESTION_ENDPOINT`, `MESSAGE_FORMAT`, `PREFIX_FILTER`. Optional: `DataFetcherTenantId/ClientId/ClientSecret`, `DataIngestorTenantId/ClientId/ClientSecret`, `DataIngestorUamiClientId`.
- **Azure Monitor authentication types** — controlled by `AZURE_MONITOR_AUTHENTICATION_TYPE` env var:
  - `DEFAULT` — uses `DefaultAzureCredential`.
  - `REMOTE_SERVICE_PRINCIPAL_AND_SECRET` — requires `DataIngestorTenantId`, `DataIngestorClientId`, `DataIngestorClientSecret`.
  - `REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_SAMI` — requires `DataIngestorTenantId`, `DataIngestorClientId`. Uses system-assigned managed identity for federation.
  - `REMOTE_SERVICE_PRINCIPAL_WITH_FEDERATION_THROUGH_UAMI` — requires `DataIngestorTenantId`, `DataIngestorClientId`, `DataIngestorUamiClientId`. Uses user-assigned managed identity for federation.
  - If unset: inferred from legacy vars (all three DataIngestor vars → secret mode; none → default; partial → startup failure).
- **Startup validation** — `JsonProcessor.ValidateMonitorAuthenticationConfig()` is called in `Program.cs` before `host.Run()`. Inconsistent configuration causes the app to fail fast with a descriptive error.
- **Structured logging** — every log call uses a `LogMessageType` enum as the first parameter (`[{logMessageType}]` prefix) for categorization in Application Insights.
- **Dual credential strategy** — both blob access and Monitor access support either explicit service principal credentials (via env vars) or `DefaultAzureCredential` fallback.
- **MESSAGE_FORMAT** controls event schema parsing: `"EventGridSchema_in_EventHub"` (unwraps `data` property) or `"EventGridSchema"` (uses event as-is).
