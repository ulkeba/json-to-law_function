Following [Tutorial: Trigger Azure Functions on blob containers using an event subscription | Microsoft Learn](https://learn.microsoft.com/en-us/azure/azure-functions/functions-event-grid-blob-trigger?pivots=programming-language-csharp)

```
func init --worker-runtime dotnet
func new --name "JsonProcessor" --template "BlobTrigger"
```