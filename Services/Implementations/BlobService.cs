using System;
using System.IO;
using System.Threading.Tasks;
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

            if (blobName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractTextFromPdf(memStream);
            }

            if (blobName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = DocX.Load(memStream);
                return doc.Text;
            }

            memStream.Position = 0;
            using var reader = new StreamReader(memStream);
            return await reader.ReadToEndAsync();
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
                    _logger.LogWarning("Moved blob {Blob} to dead-letter", blobName);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("Dead-letter skip, blob missing: {Blob}", blobName);
                }
            }

            try
            {
                var deleted = await source.DeleteIfExistsAsync();
                if (deleted)
                    _logger.LogInformation("Deleted blob {Blob}", blobName);
                else
                    _logger.LogWarning("Blob already gone: {Blob}", blobName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Cleanup skip, blob already gone: {Blob}", blobName);
            }
        }

        private string ExtractTextFromPdf(Stream stream)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var page in PdfDocument.Open(stream).GetPages())
                sb.AppendLine(page.Text);
            return sb.ToString();
        }
    }
}