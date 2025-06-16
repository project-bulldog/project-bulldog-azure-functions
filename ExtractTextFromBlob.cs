// ExtractTextFromBlob.cs
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

namespace functions
{
    public class ExtractTextFromBlob
    {
        private const string UPLOADS_CONTAINER = "uploads";
        private const string DEAD_LETTER_CONTAINER = "dead-letter";

        private readonly ILogger<ExtractTextFromBlob> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _connectionString;
        private readonly string _aiEndpoint;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ExtractTextFromBlob(
            ILogger<ExtractTextFromBlob> logger,
            IConfiguration config,
            HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
            _connectionString = config["AzureWebJobsStorage"]
                                ?? throw new InvalidOperationException("Missing AzureWebJobsStorage");
            _aiEndpoint = config["ChunkedSummaryApiEndpoint"]
                                ?? throw new InvalidOperationException("Missing ChunkedSummaryApiEndpoint");
        }

        [Function("ExtractTextFromBlob")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            // 1) parse request body
            var dto = await JsonSerializer.DeserializeAsync<BlobTriggerRequestDto>(req.Body, _jsonOptions);
            if (dto?.BlobName == null)
                return await Error(req, HttpStatusCode.BadRequest, "BlobName required");

            // 2) read blob
            string text;
            try
            {
                text = await ExtractTextAsync(dto.BlobName);
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                _logger.LogWarning(e, "Blob not found: {Blob}", dto.BlobName);
                return await Error(req, HttpStatusCode.NotFound, "Blob missing");
            }

            // 3) get token from header
            req.Headers.TryGetValues("Authorization", out var authHeaders);
            var bearer = authHeaders?.FirstOrDefault()?.Replace("Bearer ", "");

            // 4) call AI controller
            AiSummaryWithTasksResponseDto aiResult;
            try
            {
                var aiReq = new HttpRequestMessage(HttpMethod.Post, _aiEndpoint)
                {
                    Content = JsonContent.Create(new { Input = text })
                };
                if (!string.IsNullOrWhiteSpace(bearer))
                    aiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                var aiRes = await _httpClient.SendAsync(aiReq);
                aiRes.EnsureSuccessStatusCode();
                var content = await aiRes.Content.ReadAsStringAsync();
                aiResult = JsonSerializer.Deserialize<AiSummaryWithTasksResponseDto>(content, _jsonOptions)
                    ?? throw new InvalidOperationException("Empty AI response");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AI controller call failed for blob {Blob}", dto.BlobName);
                await SafeCleanup(dto.BlobName, deadLetter: true);
                return await Error(req, HttpStatusCode.InternalServerError, "AI processing failed");
            }

            // 5) cleanup
            await SafeCleanup(dto.BlobName, deadLetter: false);

            // 6) return the JSON
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(aiResult);
            return ok;
        }

        private async Task<string> ExtractTextAsync(string blobName)
        {
            var blob = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);
            await using var stream = await blob.OpenReadAsync();

            if (blobName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return await Task.Run(() =>
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var page in PdfDocument.Open(stream).GetPages())
                        sb.AppendLine(page.Text);
                    return sb.ToString();
                });
            if (blobName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                return await Task.Run(() =>
                {
                    using var mem = new MemoryStream();
                    stream.CopyTo(mem);
                    mem.Position = 0;
                    using var doc = DocX.Load(mem);
                    return doc.Text;
                });

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private async Task SafeCleanup(string blobName, bool deadLetter)
        {
            var source = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);

            if (deadLetter)
            {
                try
                {
                    var target = new BlobClient(_connectionString, DEAD_LETTER_CONTAINER, blobName);
                    await target.StartCopyFromUriAsync(source.Uri);
                    _logger.LogWarning("Moved blob {Blob} to dead-letter", blobName);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("Dead-letter skip, blob missing: {Blob}", blobName);
                }
            }

            try
            {
                var deleted = await source.DeleteIfExistsAsync();
                if (deleted)
                    _logger.LogInformation("Deleted blob {Blob}", blobName);
                else
                    _logger.LogWarning("Blob already gone: {Blob}", blobName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Cleanup skip, blob already gone: {Blob}", blobName);
            }
        }

        private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string msg)
        {
            var res = req.CreateResponse(code);
            await res.WriteStringAsync(msg);
            return res;
        }
    }
}
