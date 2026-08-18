using System.Security.Cryptography;
using RagApplication.Interfaces;

namespace RagApplication.Services
{
    public class DocumentIngestionService(
        IChunkingService chunkingService,
        IWebHostEnvironment environment,
        TextRepository textRepository,
        IPdfTextExtractor pdfTextExtractor) : IDocumentIngestionService
    {
        private readonly IChunkingService _chunkingService = chunkingService;

        private readonly IWebHostEnvironment _environment = environment;

        private readonly TextRepository _textRepository = textRepository;
        private readonly IPdfTextExtractor _pdfTextExtractor = pdfTextExtractor;

        public async Task IngestAsync()
        {
            string knowledgeBasePath =
                Path.Combine(
                    _environment.ContentRootPath,
                    "KnowledgeBase");

            if (!Directory.Exists(knowledgeBasePath))
            {
                Console.WriteLine(
                    "KnowledgeBase folder does not exist.");

                return;
            }

            string[] files =
    Directory.GetFiles(
        knowledgeBasePath);

            foreach (string file in files)
            {
                string extension =
                    Path.GetExtension(file)
                        .ToLowerInvariant();

                if (extension == ".txt" ||
                    extension == ".pdf")
                {
                    await ProcessDocumentAsync(file);
                }
            }
        }

        private async Task ProcessDocumentAsync(
     string filePath)
        {
            string documentName =
                Path.GetFileName(filePath);

            Console.WriteLine(
                $"Processing document: {documentName}");

            // ------------------------------------
            // Calculate file hash
            // ------------------------------------

            string fileHash =
                await CalculateFileHashAsync(filePath);

            Console.WriteLine(
                $"File hash: {fileHash}");

            // ------------------------------------
            // Check existing document
            // ------------------------------------

            var existingDocument =
                await _textRepository
                    .GetDocumentAsync(documentName);

            if (existingDocument != null &&
                existingDocument.FileHash == fileHash)
            {
                Console.WriteLine(
                    $"Document already indexed. " +
                    $"Skipping: {documentName}");

                return;
            }

            // ------------------------------------
            // Delete old chunks if changed
            // ------------------------------------

            if (existingDocument != null)
            {
                Console.WriteLine(
                    $"Document changed. " +
                    $"Re-ingesting: {documentName}");

                await _textRepository
                    .DeleteDocumentChunksAsync(
                        documentName);
            }

            // ------------------------------------
            // PDF
            // ------------------------------------

            if (Path.GetExtension(filePath)
                    .Equals(
                        ".pdf",
                        StringComparison.OrdinalIgnoreCase))
            {
                await ProcessPdfAsync(
                    filePath,
                    documentName);
            }

            // ------------------------------------
            // TXT
            // ------------------------------------

            else if (Path.GetExtension(filePath)
                         .Equals(
                             ".txt",
                             StringComparison.OrdinalIgnoreCase))
            {
                await ProcessTextAsync(
                    filePath,
                    documentName);
            }

            // ------------------------------------
            // Register document
            // ------------------------------------

            await _textRepository
                .RegisterDocumentAsync(
                    documentName,
                    fileHash);

            Console.WriteLine(
                $"Document indexed successfully: " +
                $"{documentName}");
        }

        private async Task ProcessPdfAsync(
    string filePath,
    string documentName)
        {
            var pages =
                await _pdfTextExtractor
                    .ExtractAsync(filePath);

            Console.WriteLine(
                $"{documentName}: " +
                $"{pages.Count} pages extracted.");

            foreach (var page in pages)
            {
                Console.WriteLine(
                    $"Processing page " +
                    $"{page.PageNumber}...");

                var chunks =
                    _chunkingService.Chunk(
                        documentName,
                        page.Content,
                        page.PageNumber);

                Console.WriteLine(
                    $"Page {page.PageNumber}: " +
                    $"{chunks.Count} chunks created.");

                foreach (var chunk in chunks)
                {
                    Console.WriteLine(
                        $"Processing chunk " +
                        $"{chunk.ChunkNumber} " +
                        $"from page " +
                        $"{chunk.PageNumber}...");

                    await _textRepository
                        .StoreChunkAsync(chunk);
                }
            }
        }

        private async Task ProcessTextAsync(
    string filePath,
    string documentName)
        {
            string text =
                await File.ReadAllTextAsync(filePath);

            var chunks =
                _chunkingService.Chunk(
                    documentName,
                    text);

            Console.WriteLine(
                $"{documentName}: " +
                $"{chunks.Count} chunks created.");

            foreach (var chunk in chunks)
            {
                Console.WriteLine(
                    $"Processing chunk " +
                    $"{chunk.ChunkNumber}...");

                await _textRepository
                    .StoreChunkAsync(chunk);
            }
        }

        private static async Task<string>
            CalculateFileHashAsync(
                string filePath)
        {
            await using var stream =
                File.OpenRead(filePath);

            using var sha256 =
                SHA256.Create();

            byte[] hash =
                await sha256.ComputeHashAsync(
                    stream);

            return Convert.ToHexString(hash);
        }
    }
}