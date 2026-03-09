using STG.Common.DTO;
using STG.Common.Interfaces;
using STG.Common.Utilities.Logging;
using STG.RT.API;
using STG.RT.API.Activity;
using STG.RT.API.Document;
using STG.RT.API.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using VertesiaActivity.Settings;

namespace VertesiaActivity
{
    /// <summary>
    /// Uploads each child document to Vertesia, waits for processing to complete,
    /// executes a configured interaction, and maps the results back to the child
    /// document as custom values.
    ///
    /// Flow per child document:
    ///   1. POST https://sts.vertesia.io/token/issue  (ApiKey → JWT)
    ///   2. POST {ApiUrl}/objects/upload-url          (→ pre-signed URL + object id)
    ///   3. PUT  {pre-signed URL}                     (upload binary)
    ///   4. POST {ApiUrl}/objects                     (register object)
    ///   5. GET  {ApiUrl}/objects/{object_id}         (poll until status == "ready")
    ///   6. POST {ApiUrl}/interactions/{InteractionId}/execute  (→ Results JSON)
    ///   7. Map Results fields → child document custom values via ResultMapping
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

            // Build the file name: "<media name>.<extension>" where extension is the
            // lowercased media type name (e.g. "pdf", "jpg").

            //Testing hard coded file name and extension
            var extension = media.MediaType?.MediaTypeName?.ToLowerInvariant() ?? "bin";
            var baseName = string.IsNullOrWhiteSpace(media.Name) ? media.ID.ToString() : media.Name;
            
            //Testing FIle Name
            var mediaFileName = $"{baseName}.{extension}";
            Log.Debug($"File name is:{mediaFileName}");
            //var mediaFileName = "7499447.pdf";

            // Use the media type's MIME type (e.g. "application/pdf") for Content-Type
            // headers and the upload-url request body.

            //Testing Media Mime Type
            var mimeType = media.MediaType?.MediaTypeMimeType;
            Log.Debug($"Mime Type is: {mimeType}");
                //            ?? $"application/{extension}";
            //var mimeType = "application/pdf";

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

            var results = ExecuteInteraction(jwt, ActivityConfiguration.ApiUrl, ActivityConfiguration.InteractionId, objectId);

            Log.Debug($"Interaction returned {results.Count} result field(s).");

            ApplyResultMapping(childDocument, results, ActivityConfiguration.ResultMapping);
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
            //I need to show the URL to ensure it's right
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

        private Dictionary<string, string> ExecuteInteraction(string jwt, string apiUrl, string interactionId, string objectId)
        {
            var url = apiUrl.TrimEnd('/') + "/interactions/" + interactionId + "/execute";
            var requestBody = JsonSerializer.Serialize(new
            {
                data = new
                {
                    document = "store:" + objectId
                }
            });

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
        // Step 7 – Map results to child document custom values
        // -------------------------------------------------------------------------

        //internal void ApplyResultMapping(
        //    STGDocument childDocument,
        //    Dictionary<string, string> results,
        //    SerializableDictionary<string, string> mapping)
        //{
        //    if (mapping == null || results == null)
        //        return;

        //    foreach (var entry in mapping)
        //    {
        //        if (results.TryGetValue(entry.Key, out var value))
        //        {
        //            childDocument.AddCustomValue(entry.Value, value, true);
        //            Log.Debug($"Mapped '{entry.Key}' → '{entry.Value}': {value}");
        //        }
        //        else
        //        {
        //            Log.Warn($"Result field '{entry.Key}' not found; custom value '{entry.Value}' was not set.");
        //        }
        //    }
        //}
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