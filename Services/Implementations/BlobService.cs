using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using Novacode;
using functions.Services.Interfaces;

namespace functions.Services.Implementations
{
    public class BlobService : IBlobService
    {
        private const string UPLOADS_CONTAINER = "uploads";
        private const string DEAD_LETTER_CONTAINER = "dead-letter";

        private readonly ILogger<BlobService> _logger;
        private readonly string _connectionString;

        public BlobService(ILogger<BlobService> logger, IConfiguration config)
        {
            _logger = logger;
            _connectionString = config["AzureWebJobsStorage"]
                ?? throw new InvalidOperationException("Missing AzureWebJobsStorage");
        }

        public async Task<string> ExtractTextAsync(string blobName)
        {
            var blob = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);

            await using var rawStream = await blob.OpenReadAsync();
            using var memStream = new MemoryStream();
            await rawStream.CopyToAsync(memStream);
            memStream.Position = 0;

            string extractedText = blobName switch
            {
                var name when name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) => ExtractTextFromPdf(memStream),
                var name when name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) => ExtractTextFromDocx(memStream),
                _ => await ReadTextFileAsync(memStream)
            };

            LogTextSampleAndTimezones(blobName, extractedText);

            return extractedText;
        }

        public async Task SafeCleanup(string blobName, bool deadLetter)
        {
            var source = new BlobClient(_connectionString, UPLOADS_CONTAINER, blobName);

            if (deadLetter)
            {
                try
                {
                    var target = new BlobClient(_connectionString, DEAD_LETTER_CONTAINER, blobName);
                    await target.StartCopyFromUriAsync(source.Uri);
                    _logger.LogWarning("📥 Moved blob {Blob} to dead-letter", blobName);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("⚠️ Dead-letter skip, blob missing: {Blob}", blobName);
                }
            }

            try
            {
                var deleted = await source.DeleteIfExistsAsync();
                _logger.LogInformation(deleted
                    ? "🧹 Deleted blob {Blob}"
                    : "⚠️ Blob already gone: {Blob}", blobName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("⚠️ Cleanup skip, blob already gone: {Blob}", blobName);
            }
        }

        #region Private Helpers

        private static string ExtractTextFromPdf(Stream stream)
        {
            var sb = new StringBuilder();
            foreach (var page in PdfDocument.Open(stream).GetPages())
                sb.AppendLine(page.Text);
            return sb.ToString();
        }

        private static string ExtractTextFromDocx(Stream stream)
        {
            using var doc = DocX.Load(stream);
            return doc.Text;
        }

        private static async Task<string> ReadTextFileAsync(Stream stream)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync();
        }

        private void LogTextSampleAndTimezones(string blobName, string text)
        {
            var sample = text[..Math.Min(500, text.Length)];
            _logger.LogInformation("📄 Extracted text sample from {BlobName}: {Sample}", blobName, sample);

            var timezoneAbbreviations = new[] { "PT", "PST", "PDT", "ET", "EST", "EDT", "CT", "CST", "CDT", "MT", "MST", "MDT", "GMT", "UTC", "Z" };
            var foundTimezones = timezoneAbbreviations
                .Where(tz => text.Contains(tz, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (foundTimezones.Any())
                _logger.LogInformation("✅ Timezone info found in text: {Timezones}", string.Join(", ", foundTimezones));
            else
                _logger.LogWarning("⚠️ No timezone info found in text");
        }

        #endregion
    }
}
