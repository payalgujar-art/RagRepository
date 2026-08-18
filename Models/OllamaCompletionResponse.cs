using System.Text.Json.Serialization;

namespace RagApplication.Models
{
    public class OllamaCompletionResponse
    {
        /// <summary>The generated text response from the LLM.</summary>
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        /// <summary>Token IDs representing conversation context (used by Ollama for multi-turn conversations).</summary>
        [JsonPropertyName("context")]
        public List<int> Context { get; set; } = [];
    }
}
