using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using functions.Dtos;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Options;
using functions.Services.Interfaces;

namespace functions
{
    public class ExtractTextFromBlob
    {
        private const string UPLOADS_CONTAINER = "uploads";
        private const string DEAD_LETTER_CONTAINER = "dead-letter";

        private readonly ILogger<ExtractTextFromBlob> _logger;
        private readonly IBlobService _blobService;
        private readonly IAIService _aiService;
        private readonly JsonSerializerOptions _jsonOptions;

        public ExtractTextFromBlob(
            ILogger<ExtractTextFromBlob> logger,
            IBlobService blobService,
            IAIService aiService,
            IOptions<JsonSerializerOptions> jsonOptions,
            IConfiguration configuration)
        {
            _logger = logger;
            _blobService = blobService;
            _aiService = aiService;
            _jsonOptions = jsonOptions.Value;
        }

        [Function("ExtractTextFromBlob")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var dto = await ParseRequestAsync(req);
            if (dto == null)
                return await Error(req, HttpStatusCode.BadRequest, "Invalid request payload");

            var bearer = ExtractBearerToken(req);
            if (string.IsNullOrWhiteSpace(bearer))
                return await Error(req, HttpStatusCode.Unauthorized, "Missing bearer token");

            var text = await TryReadBlobAsync(dto.BlobName);
            if (text == null)
                return await Error(req, HttpStatusCode.NotFound, $"Blob not found: {dto.BlobName}");

            var userTimeZoneId = ExtractUserTimeZone(req);

            var aiResult = await TryCallAiAsync(dto.BlobName, text, bearer, userTimeZoneId);
            if (aiResult == null)
                return await Error(req, HttpStatusCode.InternalServerError, "AI processing failed");

            await _blobService.SafeCleanup(dto.BlobName, deadLetter: false);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            var jsonString = JsonSerializer.Serialize(aiResult, _jsonOptions);
            await response.WriteStringAsync(jsonString);

            _logger.LogInformation("✅ AI result sent with {Count} action items. Used TimeZone: {TimeZone}", aiResult.ActionItems.Count, aiResult.UsedTimeZoneId);

            stopwatch.Stop();
            _logger.LogInformation("⏱ Function executed in {Ms} ms", stopwatch.ElapsedMilliseconds);

            return response;
        }

        private async Task<BlobTriggerRequestDto?> ParseRequestAsync(HttpRequestData req)
        {
            try
            {
                var dto = await JsonSerializer.DeserializeAsync<BlobTriggerRequestDto>(req.Body, _jsonOptions, CancellationToken.None);
                return string.IsNullOrWhiteSpace(dto?.BlobName) ? null : dto;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "❌ Failed to parse request body");
                return null;
            }
        }


        private string ExtractBearerToken(HttpRequestData req)
        {
            req.Headers.TryGetValues("Authorization", out var headers);
            return headers?.FirstOrDefault()?.Replace("Bearer ", "") ?? string.Empty;
        }

        private string? ExtractUserTimeZone(HttpRequestData req)
        {
            return req.Headers.TryGetValues("X-User-TimeZone", out var values) ? values.FirstOrDefault() : null;
        }

        private async Task<string?> TryReadBlobAsync(string blobName)
        {
            try
            {
                return await _blobService.ExtractTextAsync(blobName);
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                _logger.LogWarning(e, "Blob not found: {Blob}", blobName);
                return null;
            }
        }

        private async Task<AiSummaryWithTasksResponseDto?> TryCallAiAsync(string blobName, string text, string bearer, string? userTimeZoneId)
        {
            try
            {
                _logger.LogInformation("📡 Calling AI service for blob {BlobName}", blobName);

                var result = await _aiService.ProcessTextAsync(text, bearer, userTimeZoneId);

                if (result.ActionItems != null)
                {
                    foreach (var item in result.ActionItems)
                    {
                        _logger.LogInformation("📝 AI Result - Task: '{Text}' | DueAt: '{DueAt}' | Type: '{Type}'",
                            item.Text, item.DueAt, item.DueAt?.GetType().Name ?? "null");
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "🔥 AI controller call failed for blob {Blob}", blobName);
                await _blobService.SafeCleanup(blobName, deadLetter: true);
                return null;
            }
        }

        private async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string msg, Exception? ex = null)
        {
            if (ex != null)
                _logger.LogError(ex, "❌ {Message}", msg);
            else
                _logger.LogWarning("⚠️ {Message}", msg);

            var res = req.CreateResponse(code);
            await res.WriteStringAsync(msg);
            return res;
        }
    }
}
