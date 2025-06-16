using System.Text.Json.Serialization;

namespace functions.Dtos;

public class BlobTriggerRequestDto
{
    [JsonPropertyName("blobName")]
    public string BlobName { get; init; } = string.Empty;

    [JsonConstructor]
    public BlobTriggerRequestDto(string blobName)
    {
        BlobName = blobName;
    }
}
