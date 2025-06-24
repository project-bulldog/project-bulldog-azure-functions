using System.Text.Json.Serialization;

namespace functions.Dtos;

public record AiSummaryWithTasksResponseDto
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; }

    [JsonPropertyName("actionItems")]
    public List<ActionItemDto> ActionItems { get; init; }

    [JsonPropertyName("usedTimeZoneId")]
    public string? UsedTimeZoneId { get; init; }

    [JsonConstructor]
    public AiSummaryWithTasksResponseDto(string summary, List<ActionItemDto>? actionItems, string? usedTimeZoneId = null)
    {
        Summary = summary;
        ActionItems = actionItems ?? new List<ActionItemDto>();
        UsedTimeZoneId = usedTimeZoneId;
    }
}

