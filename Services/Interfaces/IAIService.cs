using System.Threading.Tasks;
using functions.Dtos;

namespace functions.Services.Interfaces
{
    public interface IAIService
    {
        Task<AiSummaryWithTasksResponseDto> ProcessTextAsync(string text, string bearerToken);
    }
}