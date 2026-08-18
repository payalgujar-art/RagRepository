using RagApplication.Models;

namespace RagApplication.Interfaces
{
    public interface IChunkingService
    {
        List<DocumentChunk> Chunk(
            string documentName,
            string text,
            int? pageNumber = null);
    }
}
