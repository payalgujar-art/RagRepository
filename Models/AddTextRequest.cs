namespace RagApplication.Models
{
    public class AddTextRequest
    {
        /// <summary>The text content to store. An embedding will be generated for this text.</summary>
        public string Content { get; set; } = string.Empty;
    }
}
