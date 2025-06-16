using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using functions.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using functions.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace functions.Services.Implementations
{
    public class AIService : IAIService
    {
        private readonly ILogger<AIService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _aiEndpoint;
        private readonly JsonSerializerOptions _jsonOptions;

        public AIService(
            ILogger<AIService> logger,
            IConfiguration config,
            HttpClient httpClient,
            IOptions<JsonSerializerOptions> jsonOptions)
        {
            _logger = logger;
            _httpClient = httpClient;
            _aiEndpoint = config["ChunkedSummaryApiEndpoint"]
                ?? throw new InvalidOperationException("Missing ChunkedSummaryApiEndpoint");
            _jsonOptions = jsonOptions.Value;
        }

        public async Task<AiSummaryWithTasksResponseDto> ProcessTextAsync(string text, string bearerToken)
        {
            // Create HTTP request to AI endpoint with input text
            var aiReq = new HttpRequestMessage(HttpMethod.Post, _aiEndpoint)
            {
                Content = JsonContent.Create(new { Input = text })
            };

            // Add bearer token to request headers if provided
            if (!string.IsNullOrWhiteSpace(bearerToken))
                aiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            // Send request to AI service
            var aiRes = await _httpClient.SendAsync(aiReq);

            // Handle error response from AI service
            if (!aiRes.IsSuccessStatusCode)
            {
                var error = await aiRes.Content.ReadAsStringAsync();
                _logger.LogError("❌ AI endpoint failed: {Status} {Reason} - {Body}",
                    aiRes.StatusCode, aiRes.ReasonPhrase, error);
                throw new HttpRequestException($"AI service returned {aiRes.StatusCode}: {error}");
            }

            // Read and log raw response content
            var content = await aiRes.Content.ReadAsStringAsync();
            _logger.LogInformation("Raw AI response: {Response}", content);

            // Deserialize response into DTO, throw if empty or invalid
            var result = JsonSerializer.Deserialize<AiSummaryWithTasksResponseDto>(content, _jsonOptions)
                ?? throw new InvalidOperationException("Empty or malformed AI response");

            // Log warning if no action items were returned
            if (result.ActionItems == null || result.ActionItems.Count == 0)
                _logger.LogWarning("⚠️ AI response contained no action items.");

            // Log details of each action item for debugging
            foreach (var item in result.ActionItems)
            {
                _logger.LogInformation("📝 ActionItem: '{Text}' | DueAt: '{DueAt}' | Type: '{Type}'",
                    item.Text, item.DueAt, item.DueAt?.GetType().Name ?? "null");
            }

            return result;
        }
    }
}