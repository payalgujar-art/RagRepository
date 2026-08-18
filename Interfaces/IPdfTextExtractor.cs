using RagApplication.Models;

namespace RagApplication.Interfaces
{
    public interface IPdfTextExtractor
    {
        Task<List<DocumentPage>> ExtractAsync(
            string filePath);
    }
}
