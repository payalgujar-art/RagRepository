using RagApplication.Interfaces;
using RagApplication.Models;
using UglyToad.PdfPig;

namespace RagApplication.Services
{
    public class PdfTextExtractor : IPdfTextExtractor
    {
        public Task<List<DocumentPage>> ExtractAsync(
            string filePath)
        {
            var pages = new List<DocumentPage>();

            string documentName =
                Path.GetFileName(filePath);

            using var document =
                PdfDocument.Open(filePath);

            foreach (var page in document.GetPages())
            {
                string text =
                    page.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                pages.Add(new DocumentPage
                {
                    DocumentName = documentName,

                    PageNumber = page.Number,

                    Content = text
                });
            }

            return Task.FromResult(pages);
        }
    }
}
