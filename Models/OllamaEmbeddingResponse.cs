using System.Text.Json.Serialization;

namespace RagApplication.Models
{
    public class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }
}
