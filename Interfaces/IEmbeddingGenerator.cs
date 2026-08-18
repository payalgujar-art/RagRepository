namespace RagApplication.Interfaces
{
    public interface IEmbeddingGenerator
    {
        Task<float[]> GenerateEmbeddingAsync(string text);
    }
}
