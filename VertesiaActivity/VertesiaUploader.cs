using STG.Common.DTO;
using STG.Common.Interfaces;
using STG.Common.Utilities.Logging;
using STG.RT.API;
using STG.RT.API.Activity;
using STG.RT.API.Document;
using STG.RT.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml.Linq;
using VertesiaActivity.Settings;

namespace VertesiaActivity
{
    /// <summary>
    /// Uploads each child document to Vertesia, waits for processing to complete,
    /// executes a configured interaction, and maps the results back to the child
    /// document as index fields and/or table rows.
    ///
    /// Flow per child document:
    ///   1. POST https://sts.vertesia.io/token/issue  (ApiKey → JWT)
    ///   2. POST {ApiUrl}/objects/upload-url          (→ pre-signed URL + object id)
    ///   3. PUT  {pre-signed URL}                     (upload binary)
    ///   4. POST {ApiUrl}/objects                     (register object)
    ///   5. GET  {ApiUrl}/objects/{object_id}         (poll until status == "ready")
    ///   6. POST {ApiUrl}/interactions/{InteractionId}/execute  (→ Results JSON)
    ///   7. Map Results fields → child document index fields via ResultMapping
    ///   8. Map Results arrays → child document tables via TableMapping
    /// </summary>
    public class VertesiaUploader : STGUnattendedAbstract<VertesiaUploaderSettings>
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private const string TokenIssueUrl = "https://sts.vertesia.io/token/issue";
        private const string ReadyStatus = "ready";
        private const int PollIntervalMs = 5000;
        private const int MaxPollAttempts = 60;

        private readonly HttpClient _httpClient;

        // Called by the STG framework via reflection.
        public VertesiaUploader() : this(new HttpClient()) { }

        // Called by unit tests to inject a mock handler.
        internal VertesiaUploader(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public override void Process(DtoWorkItemData workItemInProgress, STGDocument document)
        {
            foreach (var childDocument in document.ChildDocuments)
            {
                ProcessChildDocument(childDocument);
            }
        }

        // -------------------------------------------------------------------------
        // Private pipeline
        // -------------------------------------------------------------------------

        private void ProcessChildDocument(STGDocument childDocument)
        {
            var media = childDocument.Media?.Count > 0 ? childDocument.Media[0] : null;
            if (media == null)
            {
                Log.Warn($"Child document {childDocument.ID} has no media; skipping.");
                return;
            }

            var extension = media.MediaType?.MediaTypeName?.ToLowerInvariant() ?? "bin";
            var baseName = string.IsNullOrWhiteSpace(media.Name) ? media.ID.ToString() : media.Name;
            var mediaFileName = $"{baseName}.{extension}";
            Log.Debug($"File name is:{mediaFileName}");

            var mimeType = media.MediaType?.MediaTypeMimeType;
            Log.Debug($"Mime Type is: {mimeType}");

            var jwt = GetJwtToken(ActivityConfiguration.ApiKey);
            Log.Info($"JWT is: {jwt}");

            var uploadInfo = GetUploadUrl(jwt, ActivityConfiguration.ApiUrl, mediaFileName, mimeType);
            Log.Debug($"Got upload URL for object id: {uploadInfo.Id}");

            if (media.MediaStream.CanSeek)
                media.MediaStream.Seek(0, SeekOrigin.Begin);

            UploadDocument(uploadInfo.Url, media.MediaStream, mimeType);
            Log.Debug($"Uploaded child document {childDocument.ID}.");

            var registerResponse = RegisterObject(
                jwt,
                ActivityConfiguration.ApiUrl,
                ActivityConfiguration.ContentType,
                uploadInfo.Id,
                mediaFileName,
                mimeType);

            var objectId = registerResponse?.Id;
            Log.Debug($"Registered object id: {objectId}");

            WaitForObjectReady(jwt, ActivityConfiguration.ApiUrl, objectId);
            Log.Debug($"Object {objectId} is ready.");

            var results = ExecuteInteraction(jwt, ActivityConfiguration.ApiUrl, ActivityConfiguration.InteractionId, objectId, ActivityConfiguration.AdditionalParameters);
            Log.Debug($"Interaction returned {results.Count} result field(s).");

            ApplyResultMapping(childDocument, results, ActivityConfiguration.ResultMapping);
            ApplyTableMapping(childDocument, results, ActivityConfiguration.TableMapping);
        }

        // -------------------------------------------------------------------------
        // Step 1 – Authenticate
        // -------------------------------------------------------------------------

        private string GetJwtToken(string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, TokenIssueUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("token").GetString();
            Log.Debug($"Got JWT token. Value is {token}");
            return token;
        }

        // -------------------------------------------------------------------------
        // Step 2 – Request pre-signed upload URL
        // -------------------------------------------------------------------------

        private UploadUrlResponse GetUploadUrl(string jwt, string apiUrl, string mediaFileName, string mimeType)
        {
            var url = apiUrl.TrimEnd('/') + "/objects/upload-url";
            Log.Error(url);

            var body = JsonSerializer.Serialize(new { name = mediaFileName, mime_type = mimeType });
            Log.Debug($"Upload URL Body is: {body}");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            Log.Debug($"Request content is: {request.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");

            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<UploadUrlResponse>(responseBody, JsonOptions);
        }

        // -------------------------------------------------------------------------
        // Step 3 – Upload binary to pre-signed URL
        // -------------------------------------------------------------------------

        private void UploadDocument(string uploadUrl, Stream documentStream, string contentType)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            request.Content = new StreamContent(documentStream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }

        // -------------------------------------------------------------------------
        // Step 4 – Register the object in Vertesia
        // -------------------------------------------------------------------------

        private RegisterObjectResponse RegisterObject(
            string jwt,
            string apiUrl,
            string contentType,
            string sourceId,
            string fileName,
            string mimeType)
        {
            var url = apiUrl.TrimEnd('/') + "/objects";
            var body = JsonSerializer.Serialize(new
            {
                type = contentType,
                content = new
                {
                    source = sourceId,
                    name = fileName,
                    type = mimeType
                },
                properties = new { }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<RegisterObjectResponse>(responseBody, JsonOptions);
        }

        // -------------------------------------------------------------------------
        // Step 5 – Poll until status == "ready"
        // -------------------------------------------------------------------------

        private void WaitForObjectReady(string jwt, string apiUrl, string objectId)
        {
            var url = apiUrl.TrimEnd('/') + "/objects/" + objectId;

            for (var attempt = 1; attempt <= MaxPollAttempts; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var status = JsonSerializer.Deserialize<ObjectStatusResponse>(body, JsonOptions);

                if (string.Equals(status?.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase))
                    return;

                Log.Debug($"Object {objectId} status '{status?.Status}' (attempt {attempt}/{MaxPollAttempts}).");

                if (attempt < MaxPollAttempts)
                    Thread.Sleep(PollIntervalMs);
            }

            throw new InvalidOperationException(
                $"Object '{objectId}' did not reach '{ReadyStatus}' status after {MaxPollAttempts} attempts.");
        }

        // -------------------------------------------------------------------------
        // Step 6 – Execute interaction
        // -------------------------------------------------------------------------

        internal Dictionary<string, string> ExecuteInteraction(
    string jwt,
    string apiUrl,
    string interactionId,
    string objectId,
    IDictionary<string, string> additionalParameters = null)
        {
            var url = apiUrl.TrimEnd('/') + "/interactions/" + interactionId + "/execute";

            var dataDict = new Dictionary<string, object> { ["document"] = "store:" + objectId };
            if (additionalParameters != null)
                foreach (var kv in additionalParameters)
                    dataDict[kv.Key] = kv.Value;

            var requestBody = JsonSerializer.Serialize(new Dictionary<string, object> { ["data"] = dataDict });

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");



            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log.Debug($"Interaction response body: {body}");

            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var doc = JsonDocument.Parse(body))
            {
                // The live Vertesia API returns "result" (lowercase); accept "Results" as fallback.
                JsonElement resultsElement;
                bool found = doc.RootElement.TryGetProperty("result", out resultsElement)
                          || doc.RootElement.TryGetProperty("Results", out resultsElement);

                if (found && resultsElement.ValueKind == JsonValueKind.Object)
                    FlattenJsonElement(resultsElement, "", results);
            }
            Log.Debug($"Full results: {results}");

            return results;
        }

        /// <summary>
        /// Recursively flattens a <see cref="JsonElement"/> into <paramref name="target"/> using
        /// dot-notation for nested objects and bracket-index notation for arrays.
        /// Example keys produced: "invoice_number", "vendor.name", "line_items[0].description".
        /// </summary>
        private static void FlattenJsonElement(JsonElement element, string prefix, Dictionary<string, string> target)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var key = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "." + prop.Name;
                        FlattenJsonElement(prop.Value, key, target);
                    }
                    break;

                case JsonValueKind.Array:
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        var key = prefix + "[" + index + "]";
                        FlattenJsonElement(item, key, target);
                        index++;
                    }
                    break;

                default:
                    target[prefix] = element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.ToString();
                    break;
            }
        }

        // -------------------------------------------------------------------------
        // Step 7 – Map results to child document index fields
        // -------------------------------------------------------------------------

        internal void ApplyResultMapping(
            STGDocument childDocument,
            Dictionary<string, string> results,
            SerializableDictionary<string, string> mapping)
        {
            if (mapping == null || results == null)
                return;

            // Group mapping entries by document type, parsed from "DocumentType.FieldName" format.
            var grouped = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in mapping)
            {
                var dotIndex = entry.Value.IndexOf('.');
                if (dotIndex <= 0)
                {
                    Log.Warn($"Mapping value '{entry.Value}' is not in 'DocumentType.FieldName' format; skipping.");
                    continue;
                }

                var docTypeName = entry.Value.Substring(0, dotIndex);
                if (!grouped.TryGetValue(docTypeName, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    grouped[docTypeName] = list;
                }
                list.Add(entry);
            }

            foreach (var group in grouped)
            {
                childDocument.Initialize(group.Key);

                foreach (var entry in group.Value)
                {
                    if (!results.TryGetValue(entry.Key, out var value))
                    {
                        Log.Warn($"Result field '{entry.Key}' not found; field was not set.");
                        continue;
                    }

                    var dotIndex = entry.Value.IndexOf('.');
                    var fieldName = entry.Value.Substring(dotIndex + 1);

                    var field = childDocument.IndexFields.FirstOrDefault(f =>
                        string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

                    if (field != null)
                    {
                        field.FieldValue.SetText(value);
                        Log.Debug($"Mapped '{entry.Key}' → '{fieldName}' on '{group.Key}': {value}");
                    }
                    else
                    {
                        Log.Warn($"Index field '{fieldName}' not found on document type '{group.Key}'; value was not set.");
                    }
                }
            }
        }

        // -------------------------------------------------------------------------
        // Step 8 – Map results arrays to child document tables
        // -------------------------------------------------------------------------

        internal void ApplyTableMapping(
            STGDocument childDocument,
            Dictionary<string, string> results,
            SerializableDictionary<string, string> mapping)
        {
            if (mapping == null || mapping.Count == 0 || results == null)
                return;

            // Group entries by array prefix — the part of the key before the first '.'.
            // e.g. key "line_items.description" → array prefix "line_items"
            var groups = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in mapping)
            {
                var keyDot = entry.Key.IndexOf('.');
                var valDot = entry.Value.IndexOf('.');

                if (keyDot <= 0)
                {
                    Log.Warn($"Table mapping key '{entry.Key}' is not in 'array_prefix.field_name' format; skipping.");
                    continue;
                }
                if (valDot <= 0)
                {
                    Log.Warn($"Table mapping value '{entry.Value}' is not in 'TableName.ColumnName' format; skipping.");
                    continue;
                }

                var arrayPrefix = entry.Key.Substring(0, keyDot);
                if (!groups.TryGetValue(arrayPrefix, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    groups[arrayPrefix] = list;
                }
                list.Add(entry);
            }

            foreach (var group in groups)
            {
                var arrayPrefix = group.Key;
                var entries = group.Value;

                // Derive table name from the value prefix of the first entry.
                var firstValDot = entries[0].Value.IndexOf('.');
                var tableName = entries[0].Value.Substring(0, firstValDot);

                // Build a lookup: column name → result field suffix within each array element.
                // e.g. "Description" → "description"
                var columnToField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tableDef = new STGTableDefinition(tableName);
                foreach (var entry in entries)
                {
                    var keyDot = entry.Key.IndexOf('.');
                    var valDot = entry.Value.IndexOf('.');
                    var fieldSuffix = entry.Key.Substring(keyDot + 1);
                    var columnName = entry.Value.Substring(valDot + 1);
                    columnToField[columnName] = fieldSuffix;
                    tableDef.AppendColumnDefinition(columnName, DtoSTGDataType.STGString);
                }

                var table = childDocument.Tables.FirstOrDefault(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            ?? childDocument.AddDynamicTable(tableDef);



                // Iterate array elements until no mapped field is found for that index.
                for (int i = 0; ; i++)
                {
                    var indexPrefix = $"{arrayPrefix}[{i}]";

                    bool anyFound = columnToField.Values.Any(f =>
                        results.ContainsKey($"{indexPrefix}.{f}"));

                    if (!anyFound)
                        break;

                    var row = table.InsertNewRow();
                    foreach (var cell in row.Cells)
                    {
                        if (!columnToField.TryGetValue(cell.ColumnName, out var fieldSuffix))
                            continue;

                        var resultKey = $"{indexPrefix}.{fieldSuffix}";
                        if (results.TryGetValue(resultKey, out var value))
                        {
                            cell.SetCapturedValue(value);
                            cell.UnformattedValue = value;
                            
                            //document.AssignFieldValueAsCapturedValue(cell, value);
                            Log.Debug($"Mapped '{resultKey}' → '{tableName}.{cell.ColumnName}' (row {i}): {value}");
                        }
                        else
                        {
                            Log.Warn($"Result key '{resultKey}' not found; cell '{cell.ColumnName}' in row {i} was not set.");
                        }
                    }
                }
                childDocument.AssignFieldValueAsCapturedValue();
                Log.Debug($"Table '{tableName}' populated with {table.Rows.Count} row(s) from '{arrayPrefix}'.");
            }
            
        }

        // -------------------------------------------------------------------------
        // Response DTOs
        // -------------------------------------------------------------------------

        private sealed class UploadUrlResponse
        {
            [JsonPropertyName("url")]
            public string Url { get; set; }

            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("mime_type")]
            public string MimeType { get; set; }

            [JsonPropertyName("path")]
            public string Path { get; set; }
        }

        private sealed class RegisterObjectResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
        }

        private sealed class ObjectStatusResponse
        {
            [JsonPropertyName("status")]
            public string Status { get; set; }
        }
    }
}
