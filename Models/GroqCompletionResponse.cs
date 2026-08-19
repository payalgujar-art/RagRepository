namespace RagApplication.Models
{
    public class GroqCompletionResponse
    {
        public List<GroqChoice>? Choices { get; set; }
    }

    public class GroqChoice
    {
        public GroqMessage? Message { get; set; }
    }

    public class GroqMessage
    {
        public string? Content { get; set; }
    }
}
