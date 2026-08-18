namespace RagApplication.Models
{
    public class RetrievedChunk
    {
        public int Id { get; set; }

        public string DocumentName { get; set; } = string.Empty;

        public int ChunkNumber { get; set; }

        public int? PageNumber { get; set; }

        public string Content { get; set; } = string.Empty;

        // Vector search result.
        // Lower distance = more semantically similar.
        public double Distance { get; set; }

        // Keyword/full-text search score.
        // Higher score = better keyword match.
        public double KeywordScore { get; set; }

        // Position of this chunk in vector search results.
        // 1 = best vector result.
        public int VectorRank { get; set; }

        // Position of this chunk in keyword search results.
        // 1 = best keyword result.
        public int KeywordRank { get; set; }

        // Final Reciprocal Rank Fusion score.
        // Higher = better combined result.
        public double RrfScore { get; set; }
    }
}