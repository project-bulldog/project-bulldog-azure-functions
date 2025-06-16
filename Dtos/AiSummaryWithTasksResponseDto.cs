using System.Text.Json.Serialization;

namespace functions.Dtos;

public record AiSummaryWithTasksResponseDto
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; }

    [JsonPropertyName("actionItems")]
    public List<ActionItemDto> ActionItems { get; init; }

    [JsonConstructor]
    public AiSummaryWithTasksResponseDto(string summary, List<ActionItemDto>? actionItems)
    {
        Summary = summary;
        ActionItems = actionItems ?? new List<ActionItemDto>();
    }
}
