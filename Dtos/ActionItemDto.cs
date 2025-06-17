using System.Text.Json.Serialization;

namespace functions.Dtos;

public class ActionItemDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("dueAt")]
    public DateTime? DueAt { get; set; }

    [JsonPropertyName("isDateOnly")]
    public bool IsDateOnly { get; set; } = false;
}
