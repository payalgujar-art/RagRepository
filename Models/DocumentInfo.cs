namespace RagApplication.Models
{
    public class DocumentInfo
    {
        public int Id { get; set; }

        public string DocumentName { get; set; } = string.Empty;

        public string FileHash { get; set; } = string.Empty;
    }
}
