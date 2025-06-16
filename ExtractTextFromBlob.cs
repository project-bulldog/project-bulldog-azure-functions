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
using UglyToad.PdfPig;
using Novacode;
using functions.Dtos;
using System.Threading;
using System.Text.Json.Serialization;
using functions.Services.Interfaces;
using functions.Converters;
using Microsoft.Extensions.Options;

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
            IOptions<JsonSerializerOptions> jsonOptions)
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

            var text = await TryReadBlobAsync(req, dto.BlobName);
            if (text == null)
                return await Error(req, HttpStatusCode.NotFound, $"Blob not found: {dto.BlobName}");

            var aiResult = await TryCallAiAsync(req, dto.BlobName, text, bearer);
            if (aiResult == null)
                return await Error(req, HttpStatusCode.InternalServerError, "AI processing failed");

            await _blobService.SafeCleanup(dto.BlobName, deadLetter: false);

            var response = req.CreateResponse(HttpStatusCode.OK);
            var serialized = JsonSerializer.Serialize(aiResult, _jsonOptions);
            _logger.LogInformation("✅ AI result sent: {Result}", serialized);

            foreach (var item in aiResult.ActionItems)
            {
                _logger.LogInformation("📝 ActionItem: '{Text}' | DueAt: '{DueAt}' | Type: '{Type}'",
                    item.Text, item.DueAt, item.DueAt?.GetType().Name ?? "null");
            }

            stopwatch.Stop();
            _logger.LogInformation("⏱ Function executed in {Ms} ms", stopwatch.ElapsedMilliseconds);

            await response.WriteAsJsonAsync(aiResult);
            return response;
        }


        #region Helper methods
        private async Task<BlobTriggerRequestDto?> ParseRequestAsync(HttpRequestData req)
        {
            try
            {
                var dto = await JsonSerializer.DeserializeAsync<BlobTriggerRequestDto>(req.Body, _jsonOptions);
                if (string.IsNullOrWhiteSpace(dto?.BlobName))
                    return null;
                return dto;
            }
            catch (JsonException ex)
            {
                await Error(req, HttpStatusCode.BadRequest, "Invalid JSON payload", ex);
                return null;
            }
        }

        private string ExtractBearerToken(HttpRequestData req)
        {
            req.Headers.TryGetValues("Authorization", out var headers);
            return headers?.FirstOrDefault()?.Replace("Bearer ", "") ?? string.Empty;
        }

        private async Task<string?> TryReadBlobAsync(HttpRequestData req, string blobName)
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

        private async Task<AiSummaryWithTasksResponseDto?> TryCallAiAsync(HttpRequestData req, string blobName, string text, string bearer)
        {
            try
            {
                return await _aiService.ProcessTextAsync(text, bearer);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AI controller call failed for blob {Blob}", blobName);
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

        #endregion
    }
}
