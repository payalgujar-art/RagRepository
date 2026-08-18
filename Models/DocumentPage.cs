namespace RagApplication.Models
{
    public class DocumentPage
    {
        public string DocumentName { get; set; } = string.Empty;

        public int PageNumber { get; set; }

        public string Content { get; set; } = string.Empty;
    }
}
