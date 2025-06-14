using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Configuration;

namespace functions;

public class ExtractTextFromBlob
{
    private readonly ILogger<ExtractTextFromBlob> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    public ExtractTextFromBlob(ILogger<ExtractTextFromBlob> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _httpClient = new HttpClient();
    }

    private record AiChunkedSummaryRequest(string Input, Guid UserId, bool? UseMapReduce = true, string? Model = null);

    [Function(nameof(ExtractTextFromBlob))]
    public async Task Run(
        [BlobTrigger("uploads/{name}", Connection = "AzureWebJobsStorage")] Stream stream,
        string name)
    {
        try
        {
            _logger.LogInformation("📥 Blob received: {name} (size: {size} bytes)", name, stream.Length);

            using var reader = new StreamReader(stream);
            var extractedText = await reader.ReadToEndAsync();
            _logger.LogInformation("🧠 Extracted {length} characters from blob {name}", extractedText.Length, name);

            var segments = name.Split('/');
            if (segments.Length < 2 || !Guid.TryParse(segments[0], out var userId))
            {
                _logger.LogWarning("⚠️ Blob path does not contain a valid userId. Blob name: {name}", name);
                return;
            }

            var connectionString = _config["AzureWebJobsStorage"];
            var blobClient = new BlobClient(connectionString, "uploads", name);
            var blobProps = await blobClient.GetPropertiesAsync();
            var metadata = blobProps.Value.Metadata;

            if (!metadata.TryGetValue("authorization", out var authHeader) || string.IsNullOrWhiteSpace(authHeader))
            {
                _logger.LogWarning("⚠️ No Authorization header found in blob metadata.");
                return;
            }

            var endpoint = _config["ChunkedSummaryApiEndpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogError("❌ ChunkedSummaryApiEndpoint not found in environment.");
                return;
            }

            var payload = new AiChunkedSummaryRequest(
                Input: extractedText,
                UserId: userId,
                UseMapReduce: true,
                Model: null
            );

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };

            // ✅ Attach token
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeader.Replace("Bearer ", ""));

            var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("✅ Successfully posted extracted text for {name}. Response: {response}", name, body);
            }
            else
            {
                _logger.LogWarning("❌ Backend rejected {name}: {status}\n{body}", name, response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Failed to process blob {name}", name);
        }
    }
}
