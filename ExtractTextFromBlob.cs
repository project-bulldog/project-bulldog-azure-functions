using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using functions.Dtos;
using UglyToad.PdfPig;
using System.IO;
using Novacode;


namespace functions;

/// <summary>
/// Azure Function that extracts text from a blob and processes it through an AI summarization service.
/// Handles blob cleanup and retries on failure.
/// </summary>
public class ExtractTextFromBlob
{
    private const string UPLOADS_CONTAINER = "uploads";
    private const string DEAD_LETTER_CONTAINER = "dead-letter";
    private const int MAX_RETRIES = 3;
    private const int RETRY_DELAY_MS = 2000;

    private readonly ILogger<ExtractTextFromBlob> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly string _connectionString;
    private readonly string _backendEndpoint;

    public ExtractTextFromBlob(ILogger<ExtractTextFromBlob> logger, IConfiguration config, HttpClient httpClient)
    {
        _logger = logger;
        _config = config;
        _httpClient = httpClient;

        _connectionString = _config["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("Missing AzureWebJobsStorage");
        _backendEndpoint = _config["ChunkedSummaryApiEndpoint"]
            ?? throw new InvalidOperationException("Missing ChunkedSummaryApiEndpoint");
    }

    [Function("ExtractTextFromBlob")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var requestBody = await JsonSerializer.DeserializeAsync<BlobTriggerRequestDto>(req.Body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (requestBody?.BlobName is null)
        {
            return await CreateResponse(req, System.Net.HttpStatusCode.BadRequest, "BlobName is required.");
        }

        var token = req.Headers.TryGetValues("Authorization", out var authHeaders)
            ? authHeaders.FirstOrDefault()?.Replace("Bearer ", "")
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return await CreateResponse(req, System.Net.HttpStatusCode.Unauthorized, "Missing Authorization token.");
        }

        try
        {
            (Guid userId, string extractedText) = await ExtractTextAndValidateUserAsync(requestBody.BlobName);
            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(extractedText))
            {
                return await CreateResponse(req, System.Net.HttpStatusCode.BadRequest, "Invalid blob path or empty text.");
            }

            var success = await ProcessTextWithRetryAsync(requestBody.BlobName, extractedText, userId, token);
            await HandleBlobCleanupAsync(requestBody.BlobName, success);

            return await CreateResponse(req, System.Net.HttpStatusCode.OK, "Blob processed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Unexpected failure while processing blob {name}", requestBody.BlobName);
            await HandleBlobCleanupAsync(requestBody.BlobName, false);
            return await CreateResponse(req, System.Net.HttpStatusCode.InternalServerError, "Error occurred.");
        }
    }

    #region Private Helpers
    /// <summary>
    /// Extracts text from a blob and validates the user ID from the blob path.
    /// </summary>
    /// <param name="blobName">Name of the blob to process</param>
    /// <returns>Tuple containing the user ID and extracted text</returns>
    private async Task<(Guid userId, string text)> ExtractTextAndValidateUserAsync(string blobName)
    {
        var blobClient = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);
        string extractedText;

        using (var stream = await blobClient.OpenReadAsync())
        {
            if (blobName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                extractedText = await ExtractTextFromPdfAsync(stream);
            }
            else if (blobName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                extractedText = await ExtractTextFromDocxAsync(stream);
            }
            else // Handle .txt and .md files as plain text
            {
                using var reader = new StreamReader(stream);
                extractedText = await reader.ReadToEndAsync();
            }
        }

        _logger.LogInformation("🧠 Extracted {length} characters from blob {name}", extractedText.Length, blobName);

        var segments = blobName.Split('/');
        if (segments.Length < 2 || !Guid.TryParse(segments[0], out var userId))
        {
            _logger.LogWarning("⚠️ Blob path does not contain a valid userId. Blob name: {name}", blobName);
            return (Guid.Empty, string.Empty);
        }

        return (userId, extractedText);
    }

    private async Task<string> ExtractTextFromPdfAsync(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var textBuilder = new System.Text.StringBuilder();

        foreach (var page in document.GetPages())
        {
            textBuilder.AppendLine(page.Text);
        }

        return textBuilder.ToString();
    }

    private async Task<string> ExtractTextFromDocxAsync(Stream docxStream)
    {
        using var mem = new MemoryStream();
        await docxStream.CopyToAsync(mem);
        mem.Position = 0;

        return await Task.Run(() =>
        {
            using var doc = DocX.Load(mem);
            return doc.Text;
        });
    }

    /// <summary>
    /// Processes the extracted text through the AI summarization service with retry logic.
    /// </summary>
    /// <param name="blobName">Name of the blob being processed</param>
    /// <param name="text">Text to be processed</param>
    /// <param name="userId">User ID associated with the request</param>
    /// <param name="token">Authorization token</param>
    /// <returns>True if processing was successful, false otherwise</returns>
    private async Task<bool> ProcessTextWithRetryAsync(string blobName, string text, Guid userId, string token)
    {
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                var payload = new AiChunkedSummaryRequestDto(text, userId, true, null);
                var request = new HttpRequestMessage(HttpMethod.Post, _backendEndpoint)
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("✅ Successfully posted extracted text for {name} on attempt {attempt}.", blobName, attempt);
                    return true;
                }

                if (attempt < MAX_RETRIES)
                {
                    _logger.LogWarning("🔁 Retry {attempt}/{maxRetries} failed for {name}. Status: {status} | Body: {body}",
                        attempt, MAX_RETRIES, blobName, response.StatusCode, body);
                    await Task.Delay(RETRY_DELAY_MS);
                }
                else
                {
                    _logger.LogError("❌ Final attempt failed for {name}. Status: {status} | Body: {body}",
                        blobName, response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error on attempt {attempt}/{maxRetries}", attempt, MAX_RETRIES);
                if (attempt < MAX_RETRIES)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Handles cleanup of processed blobs, moving failed blobs to dead-letter container.
    /// </summary>
    /// <param name="blobName">Name of the blob to clean up</param>
    /// <param name="success">Whether the blob processing was successful</param>
    private async Task HandleBlobCleanupAsync(string blobName, bool success)
    {
        try
        {
            var sourceBlob = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);

            if (!success)
            {
                try
                {
                    var deadLetterBlob = new BlobClient(_connectionString, DEAD_LETTER_CONTAINER, blobName);
                    await deadLetterBlob.StartCopyFromUriAsync(sourceBlob.Uri);
                    _logger.LogWarning("☠️ Moved blob {name} to dead-letter container", blobName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to move blob {name} to dead-letter container", blobName);
                }
            }

            try
            {
                await sourceBlob.DeleteIfExistsAsync();
                _logger.LogInformation("🗑️ Deleted blob {name} after processing", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to delete blob {name}", blobName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during blob cleanup for {name}", blobName);
        }
    }

    /// <summary>
    /// Creates an HTTP response with the specified status code and message.
    /// </summary>
    /// <param name="req">Original HTTP request</param>
    /// <param name="code">HTTP status code</param>
    /// <param name="message">Response message</param>
    /// <returns>HTTP response data</returns>
    private async Task<HttpResponseData> CreateResponse(HttpRequestData req, System.Net.HttpStatusCode code, string message)
    {
        var response = req.CreateResponse(code);
        await response.WriteStringAsync(message);
        return response;
    }
    #endregion
}
