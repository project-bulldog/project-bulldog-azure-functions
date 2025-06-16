using System.Threading.Tasks;

namespace functions.Services.Interfaces
{
    public interface IBlobService
    {
        Task<string> ExtractTextAsync(string blobName);
        Task SafeCleanup(string blobName, bool deadLetter);
    }
}