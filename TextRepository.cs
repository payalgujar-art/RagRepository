using Npgsql;
using RagApplication.Interfaces;
using RagApplication.Models;
using System.Globalization;

namespace RagApplication
{
    public class TextRepository(
        string connectionString,
        IEmbeddingGenerator embeddingGenerator)
    {
        private readonly string _connectionString = connectionString;
        private readonly IEmbeddingGenerator _embeddingGenerator =
            embeddingGenerator;

        public async Task StoreChunkAsync(
            DocumentChunk chunk)
        {
            // Generate embedding for this chunk
            var embedding =
                await _embeddingGenerator
                    .GenerateEmbeddingAsync(chunk.Content);

            Console.WriteLine(
                $"Embedding generated for " +
                $"{chunk.DocumentName} - " +
                $"Chunk {chunk.ChunkNumber}");

            // Convert float[] to PostgreSQL vector format
            string embeddingString =
                $"[{string.Join(",", embedding.Select(v =>
                    v.ToString(
                        "G",
                        CultureInfo.InvariantCulture)))}]";

            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            const string sql = """
                INSERT INTO document_chunks
                (
                    document_name,
                    chunk_number,
                    page_number,
                    content,
                    embedding
                )
                VALUES
                (
                    @documentName,
                    @chunkNumber,
                    @pageNumber,
                    @content,
                    CAST(@embedding AS vector)
                )
                """;

            await using var cmd =
                new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue(
                "documentName",
                chunk.DocumentName);

            cmd.Parameters.AddWithValue(
                "chunkNumber",
                chunk.ChunkNumber);

            cmd.Parameters.AddWithValue(
                "pageNumber",
                (object?)chunk.PageNumber ?? DBNull.Value);

            cmd.Parameters.AddWithValue(
                "content",
                chunk.Content);

            cmd.Parameters.AddWithValue(
                "embedding",
                embeddingString);

            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine(
                $"Stored: {chunk.DocumentName} " +
                $"Chunk {chunk.ChunkNumber}");
        }

        public async Task<List<RetrievedChunk>> RetrieveRelevantChunks(
    string query,
    int topK = 5,
    double maxDistance = 0.35)
        {
            // -----------------------------------------
            // Generate embedding for the query
            // -----------------------------------------

            var queryEmbedding =
                await _embeddingGenerator
                    .GenerateEmbeddingAsync(query);

            Console.WriteLine(
                $"Query embedding dimensions: " +
                $"{queryEmbedding.Length}");

            string embeddingString =
                $"[{string.Join(",", queryEmbedding.Select(v =>
                    v.ToString(
                        "G",
                        CultureInfo.InvariantCulture)))}]";

            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            // -----------------------------------------
            // RRF SQL
            // -----------------------------------------

            const string sql = """
        WITH vector_candidates AS
        (
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,
                content,

                embedding <=> CAST(@queryEmbedding AS vector)
                    AS distance

            FROM document_chunks

            WHERE embedding <=> CAST(@queryEmbedding AS vector)
                <= @maxDistance

            ORDER BY distance ASC

            LIMIT @candidateLimit
        ),

        vector_ranked AS
        (
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,
                content,
                distance,

                ROW_NUMBER() OVER (
                    ORDER BY distance ASC
                ) AS vector_rank

            FROM vector_candidates
        ),

        keyword_candidates AS
        (
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,
                content,

                ts_rank_cd(
                    search_vector,
                    websearch_to_tsquery(
                        'english',
                        @query
                    )
                ) AS keyword_score

            FROM document_chunks

            WHERE search_vector @@
                websearch_to_tsquery(
                    'english',
                    @query
                )

            ORDER BY keyword_score DESC

            LIMIT @candidateLimit
        ),

        keyword_ranked AS
        (
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,
                content,
                keyword_score,

                ROW_NUMBER() OVER (
                    ORDER BY keyword_score DESC
                ) AS keyword_rank

            FROM keyword_candidates
        ),

        combined_results AS
        (
            SELECT
                COALESCE(v.id, k.id) AS id,

                COALESCE(
                    v.document_name,
                    k.document_name
                ) AS document_name,

                COALESCE(
                    v.chunk_number,
                    k.chunk_number
                ) AS chunk_number,

                COALESCE(
                    v.page_number,
                    k.page_number
                ) AS page_number,

                COALESCE(
                    v.content,
                    k.content
                ) AS content,

                COALESCE(
                    v.distance,
                    999.0
                ) AS distance,

                COALESCE(
                    k.keyword_score,
                    0
                ) AS keyword_score,

                COALESCE(
                    v.vector_rank,
                    0
                ) AS vector_rank,

                COALESCE(
                    k.keyword_rank,
                    0
                ) AS keyword_rank

            FROM vector_ranked v

            FULL OUTER JOIN keyword_ranked k
                ON v.id = k.id
        )

        SELECT
            id,
            document_name,
            chunk_number,
            page_number,
            content,
            distance,
            keyword_score,
            vector_rank,
            keyword_rank,

            (
                CASE
                    WHEN vector_rank > 0
                    THEN 1.0 / (@rrfK + vector_rank)
                    ELSE 0
                END

                +

                CASE
                    WHEN keyword_rank > 0
                    THEN 1.0 / (@rrfK + keyword_rank)
                    ELSE 0
                END
            ) AS rrf_score

        FROM combined_results

        ORDER BY rrf_score DESC

        LIMIT @topK
        """;

            await using var cmd =
                new NpgsqlCommand(sql, conn);

            // Query embedding
            cmd.Parameters.AddWithValue(
                "queryEmbedding",
                embeddingString);

            // Vector distance threshold
            cmd.Parameters.AddWithValue(
                "maxDistance",
                maxDistance);

            // Original query for PostgreSQL full-text search
            cmd.Parameters.AddWithValue(
                "query",
                query);

            // Retrieve more candidates than final topK
            cmd.Parameters.AddWithValue(
                "candidateLimit",
                topK * 3);

            // Standard RRF constant
            cmd.Parameters.AddWithValue(
                "rrfK",
                60);

            // Final number of chunks
            cmd.Parameters.AddWithValue(
                "topK",
                topK);

            // -----------------------------------------
            // Execute query
            // -----------------------------------------

            await using var reader =
                await cmd.ExecuteReaderAsync();

            var results =
                new List<RetrievedChunk>();

            while (await reader.ReadAsync())
            {
                results.Add(
                    new RetrievedChunk
                    {
                        Id =
                            reader.GetInt32(0),

                        DocumentName =
                            reader.GetString(1),

                        ChunkNumber =
                            reader.GetInt32(2),

                        PageNumber =
                            reader.IsDBNull(3)
                                ? null
                                : reader.GetInt32(3),

                        Content =
                            reader.GetString(4),

                        Distance =
                            reader.GetDouble(5),

                        KeywordScore =
                            reader.GetDouble(6),

                        VectorRank =
                            reader.GetInt32(7),

                        KeywordRank =
                            reader.GetInt32(8),

                        RrfScore =
                            reader.GetDouble(9)
                    });
            }

            // -----------------------------------------
            // Logging
            // -----------------------------------------

            Console.WriteLine(
                $"Retrieved RRF chunks: {results.Count}");

            foreach (var result in results)
            {
                Console.WriteLine(
                    $"Source: {result.DocumentName}, " +
                    $"Chunk: {result.ChunkNumber}, " +
                    $"Page: {result.PageNumber}, " +
                    $"Distance: {result.Distance:F4}, " +
                    $"KeywordScore: {result.KeywordScore:F4}, " +
                    $"VectorRank: {result.VectorRank}, " +
                    $"KeywordRank: {result.KeywordRank}, " +
                    $"RrfScore: {result.RrfScore:F6}");
            }

            return results;
        }

        public async Task<DocumentInfo?> GetDocumentAsync(
       string documentName)
        {
            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            const string sql = """
        SELECT
            id,
            document_name,
            file_hash
        FROM documents
        WHERE document_name = @documentName
        """;

            await using var cmd =
                new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue(
                "documentName",
                documentName);

            await using var reader =
                await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new DocumentInfo
            {
                Id = reader.GetInt32(0),

                DocumentName =
                    reader.GetString(1),

                FileHash =
                    reader.GetString(2)
            };
        }

        public async Task RegisterDocumentAsync(
    string documentName,
    string fileHash)
        {
            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            const string sql = """
        INSERT INTO documents
        (
            document_name,
            file_hash
        )
        VALUES
        (
            @documentName,
            @fileHash
        )
        ON CONFLICT (document_name)
        DO UPDATE SET
            file_hash = EXCLUDED.file_hash,
            updated_at = CURRENT_TIMESTAMP
        """;

            await using var cmd =
                new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue(
                "documentName",
                documentName);

            cmd.Parameters.AddWithValue(
                "fileHash",
                fileHash);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DeleteDocumentChunksAsync(
       string documentName)
        {
            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            const string sql = """
        DELETE FROM document_chunks
        WHERE document_name = @documentName
        """;

            await using var cmd =
                new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue(
                "documentName",
                documentName);

            await cmd.ExecuteNonQueryAsync();
        }
    }
}