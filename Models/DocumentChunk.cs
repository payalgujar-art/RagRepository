namespace RagApplication.Models
{
    public class DocumentChunk
    {
        public string DocumentName { get; set; } = string.Empty;

        public int ChunkNumber { get; set; }

        public int? PageNumber { get; set; }

        public string Content { get; set; } = string.Empty;
    }
}
