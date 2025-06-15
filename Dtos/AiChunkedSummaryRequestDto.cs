namespace functions.Dtos;

public record AiChunkedSummaryRequestDto(
    string Input,
    Guid UserId,
    bool UseMapReduce,
    string? Model
);