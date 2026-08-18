using RagApplication.Interfaces;
using RagApplication.Models;

namespace RagApplication.Services
{
    public class ChunkingService : IChunkingService
    {
        private const int ChunkSize = 1000;

        private const int Overlap = 200;

        public List<DocumentChunk> Chunk(
            string documentName,
            string text,
            int? pageNumber = null)
        {
            var chunks =
                new List<DocumentChunk>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            int start = 0;

            int chunkNumber = 1;

            while (start < text.Length)
            {
                int length =
                    Math.Min(
                        ChunkSize,
                        text.Length - start);

                string content =
                    text.Substring(
                        start,
                        length)
                    .Trim();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    chunks.Add(
                        new DocumentChunk
                        {
                            DocumentName =
                                documentName,

                            ChunkNumber =
                                chunkNumber,

                            PageNumber =
                                pageNumber,

                            Content =
                                content
                        });
                }

                chunkNumber++;

                if (start + length >= text.Length)
                {
                    break;
                }

                start +=
                    ChunkSize - Overlap;
            }

            return chunks;
        }
    }
}