namespace functions.Dtos;

public record AiSummaryWithTasksResponseDto(
    string Summary,
    List<string> ActionItems
);