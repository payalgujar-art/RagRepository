using Npgsql;
using RagApplication.Interfaces;
using RagApplication.Models;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RagApplication
{
    public class TextRepository(
        string connectionString,
        IEmbeddingGenerator embeddingGenerator,
        ILogger<TextRepository> logger)
    {
        private readonly string _connectionString = connectionString;

        private readonly IEmbeddingGenerator _embeddingGenerator =
            embeddingGenerator;

        private readonly ILogger<TextRepository> _logger = logger;

        public async Task StoreChunkAsync(
            DocumentChunk chunk)
        {
            // -----------------------------------------
            // Total timer
            // -----------------------------------------

            var totalStopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "Storing chunk started. Document: {DocumentName}, Chunk: {ChunkNumber}",
                chunk.DocumentName,
                chunk.ChunkNumber);

            // -----------------------------------------
            // Generate embedding for this chunk
            // -----------------------------------------

            var embeddingStopwatch = Stopwatch.StartNew();

            var embedding =
                await _embeddingGenerator
                    .GenerateEmbeddingAsync(chunk.Content);

            embeddingStopwatch.Stop();

            _logger.LogInformation(
                "Chunk embedding generated in {ElapsedMs} ms. " +
                "Document: {DocumentName}, Chunk: {ChunkNumber}, Dimensions: {Dimensions}",
                embeddingStopwatch.ElapsedMilliseconds,
                chunk.DocumentName,
                chunk.ChunkNumber,
                embedding.Length);

            // -----------------------------------------
            // Convert float[] to PostgreSQL vector format
            // -----------------------------------------

            var conversionStopwatch = Stopwatch.StartNew();

            string embeddingString =
                $"[{string.Join(",", embedding.Select(v =>
                    v.ToString(
                        "G",
                        CultureInfo.InvariantCulture)))}]";

            conversionStopwatch.Stop();

            _logger.LogDebug(
                "Embedding conversion completed in {ElapsedMs} ms.",
                conversionStopwatch.ElapsedMilliseconds);

            // -----------------------------------------
            // Open PostgreSQL connection
            // -----------------------------------------

            var connectionStopwatch = Stopwatch.StartNew();

            await using var conn =
                new NpgsqlConnection(_connectionString);

            await conn.OpenAsync();

            connectionStopwatch.Stop();

            _logger.LogInformation(
                "PostgreSQL connection opened in {ElapsedMs} ms.",
                connectionStopwatch.ElapsedMilliseconds);

            // -----------------------------------------
            // SQL
            // -----------------------------------------

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

            // -----------------------------------------
            // Execute INSERT
            // -----------------------------------------

            var databaseStopwatch = Stopwatch.StartNew();

            await cmd.ExecuteNonQueryAsync();

            databaseStopwatch.Stop();

            _logger.LogInformation(
                "Chunk stored in PostgreSQL in {ElapsedMs} ms. " +
                "Document: {DocumentName}, Chunk: {ChunkNumber}",
                databaseStopwatch.ElapsedMilliseconds,
                chunk.DocumentName,
                chunk.ChunkNumber);

            // -----------------------------------------
            // Total time
            // -----------------------------------------

            totalStopwatch.Stop();

            _logger.LogInformation(
                "StoreChunk completed in {ElapsedMs} ms. " +
                "Document: {DocumentName}, Chunk: {ChunkNumber}",
                totalStopwatch.ElapsedMilliseconds,
                chunk.DocumentName,
                chunk.ChunkNumber);
        }

        public async Task<List<RetrievedChunk>> RetrieveRelevantChunks(
      string query,
      int topK = 5,
      double maxDistance = 0.8)
        {
            // -----------------------------------------
            // Total timer
            // -----------------------------------------

            var totalStopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "RAG retrieval started. Query: '{Query}', TopK: {TopK}, MaxDistance: {MaxDistance}",
                query,
                topK,
                maxDistance);

            // -----------------------------------------
            // Validate query
            // -----------------------------------------

            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.LogWarning(
                    "RAG retrieval skipped because query is empty.");

                return [];
            }

            if (topK <= 0)
            {
                _logger.LogWarning(
                    "Invalid TopK value: {TopK}",
                    topK);

                return [];
            }

            try
            {
                // -----------------------------------------
                // 1. Generate embedding for the query
                // -----------------------------------------

                var embeddingStopwatch =
                    Stopwatch.StartNew();

                var queryEmbedding =
                    await _embeddingGenerator
                        .GenerateEmbeddingAsync(query);

                embeddingStopwatch.Stop();

                _logger.LogInformation(
                    "Query embedding generated in {ElapsedMs} ms. Dimensions: {Dimensions}",
                    embeddingStopwatch.ElapsedMilliseconds,
                    queryEmbedding.Length);

                // Do NOT log the entire 768-dimensional embedding.

                _logger.LogInformation(
                    "Query: {Query}",
                    query);

                _logger.LogInformation(
                    "Query embedding dimensions: {Dimensions}",
                    queryEmbedding.Length);

                // -----------------------------------------
                // 2. Validate embedding
                // -----------------------------------------

                if (queryEmbedding.Length == 0)
                {
                    _logger.LogWarning(
                        "Query embedding is empty.");

                    return [];
                }

                // 3. Convert embedding to PostgreSQL vector

                var conversionStopwatch =
                    Stopwatch.StartNew();

                string embeddingString =
                    $"[{string.Join(
                        ",",
                        queryEmbedding.Select(v =>
                            v.ToString(
                                "G",
                                CultureInfo.InvariantCulture)))}]";

                conversionStopwatch.Stop();

                _logger.LogDebug(
                    "Query embedding conversion completed in {ElapsedMs} ms.",
                    conversionStopwatch.ElapsedMilliseconds);

                // 4. Open PostgreSQL connection
                

                var connectionStopwatch =
                    Stopwatch.StartNew();

                await using var conn =
                    new NpgsqlConnection(_connectionString);

                await conn.OpenAsync();

                connectionStopwatch.Stop();

                _logger.LogInformation(
                    "PostgreSQL connection opened in {ElapsedMs} ms.",
                    connectionStopwatch.ElapsedMilliseconds);

                // 5. Check total number of chunks

                var countStopwatch =
                    Stopwatch.StartNew();

                const string countSql = """
            SELECT COUNT(*)
            FROM document_chunks
            """;

                await using (
                    var countCommand =
                        new NpgsqlCommand(countSql, conn))
                {
                    var totalChunks =
                        Convert.ToInt64(
                            await countCommand.ExecuteScalarAsync());

                    countStopwatch.Stop();

                    _logger.LogInformation(
                        "Total document chunks in database: {TotalChunks}. " +
                        "Count query completed in {ElapsedMs} ms.",
                        totalChunks,
                        countStopwatch.ElapsedMilliseconds);
                }


                var vectorDebugStopwatch =
                    Stopwatch.StartNew();

                const string vectorCountSql = """
            SELECT COUNT(*)
            FROM document_chunks
            WHERE embedding <=> CAST(@queryEmbedding AS vector)
                  <= @maxDistance
            """;

                int vectorCandidateCount;

                await using (
                    var vectorCountCommand =
                        new NpgsqlCommand(vectorCountSql, conn))
                {
                    vectorCountCommand.Parameters.AddWithValue(
                        "queryEmbedding",
                        embeddingString);

                    vectorCountCommand.Parameters.AddWithValue(
                        "maxDistance",
                        maxDistance);

                    vectorCandidateCount =
                        Convert.ToInt32(
                            await vectorCountCommand
                                .ExecuteScalarAsync());
                }

                vectorDebugStopwatch.Stop();

                _logger.LogInformation(
                    "VECTOR DEBUG: {Count} chunks found within maxDistance {MaxDistance}. " +
                    "Completed in {ElapsedMs} ms.",
                    vectorCandidateCount,
                    maxDistance,
                    vectorDebugStopwatch.ElapsedMilliseconds);

                var closestVectorStopwatch =
                    Stopwatch.StartNew();

                const string closestVectorSql = """
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,
                embedding <=> CAST(@queryEmbedding AS vector)
                    AS distance
            FROM document_chunks
            WHERE embedding IS NOT NULL
            ORDER BY distance ASC
            LIMIT 5
            """;

                await using (
                    var closestVectorCommand =
                        new NpgsqlCommand(
                            closestVectorSql,
                            conn))
                {
                    closestVectorCommand.Parameters.AddWithValue(
                        "queryEmbedding",
                        embeddingString);

                    await using var closestReader =
                        await closestVectorCommand
                            .ExecuteReaderAsync();

                    while (await closestReader.ReadAsync())
                    {
                        int id =
                            closestReader.GetInt32(0);

                        string documentName =
                            closestReader.GetString(1);

                        int chunkNumber =
                            closestReader.GetInt32(2);

                        int? pageNumber =
                            closestReader.IsDBNull(3)
                                ? null
                                : closestReader.GetInt32(3);

                        double distance =
                            closestReader.GetDouble(4);

                        _logger.LogInformation(
                            "VECTOR DEBUG: Id={Id}, Document={DocumentName}, " +
                            "Chunk={ChunkNumber}, Page={PageNumber}, Distance={Distance:F6}",
                            id,
                            documentName,
                            chunkNumber,
                            pageNumber,
                            distance);
                    }
                }

                closestVectorStopwatch.Stop();

                _logger.LogInformation(
                    "Closest vector debug query completed in {ElapsedMs} ms.",
                    closestVectorStopwatch.ElapsedMilliseconds);

                var keywordDebugStopwatch =
                    Stopwatch.StartNew();

                const string keywordCountSql = """
            SELECT COUNT(*)
            FROM document_chunks
            WHERE search_vector @@
                  websearch_to_tsquery(
                      'english',
                      @query
                  )
            """;

                int keywordCandidateCount;

                await using (
                    var keywordCountCommand =
                        new NpgsqlCommand(
                            keywordCountSql,
                            conn))
                {
                    keywordCountCommand.Parameters.AddWithValue(
                        "query",
                        query);

                    keywordCandidateCount =
                        Convert.ToInt32(
                            await keywordCountCommand
                                .ExecuteScalarAsync());
                }

                keywordDebugStopwatch.Stop();

                _logger.LogInformation(
                    "KEYWORD DEBUG: {Count} chunks found for query '{Query}'. " +
                    "Completed in {ElapsedMs} ms.",
                    keywordCandidateCount,
                    query,
                    keywordDebugStopwatch.ElapsedMilliseconds);


                const string keywordResultsSql = """
            SELECT
                id,
                document_name,
                chunk_number,
                page_number,

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

            LIMIT 5
            """;

                await using (
                    var keywordResultsCommand =
                        new NpgsqlCommand(
                            keywordResultsSql,
                            conn))
                {
                    keywordResultsCommand.Parameters.AddWithValue(
                        "query",
                        query);

                    await using var keywordReader =
                        await keywordResultsCommand
                            .ExecuteReaderAsync();

                    while (await keywordReader.ReadAsync())
                    {
                        int id =
                            keywordReader.GetInt32(0);

                        string documentName =
                            keywordReader.GetString(1);

                        int chunkNumber =
                            keywordReader.GetInt32(2);

                        int? pageNumber =
                            keywordReader.IsDBNull(3)
                                ? null
                                : keywordReader.GetInt32(3);

                        double keywordScore =
                            keywordReader.GetDouble(4);

                        _logger.LogInformation(
                            "KEYWORD DEBUG: Id={Id}, Document={DocumentName}, " +
                            "Chunk={ChunkNumber}, Page={PageNumber}, Score={Score:F6}",
                            id,
                            documentName,
                            chunkNumber,
                            pageNumber,
                            keywordScore);
                    }
                }

                
                // 10. RRF SQL

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

                // 11. Parameters

                cmd.Parameters.AddWithValue(
                    "queryEmbedding",
                    embeddingString);

                cmd.Parameters.AddWithValue(
                    "maxDistance",
                    maxDistance);

                cmd.Parameters.AddWithValue(
                    "query",
                    query);

                cmd.Parameters.AddWithValue(
                    "candidateLimit",
                    topK * 3);

                cmd.Parameters.AddWithValue(
                    "rrfK",
                    60);

                cmd.Parameters.AddWithValue(
                    "topK",
                    topK);

                // 12. Execute RRF query

                var databaseStopwatch =
                    Stopwatch.StartNew();

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

                databaseStopwatch.Stop();

                _logger.LogInformation(
                    "RRF PostgreSQL query and result mapping completed in {ElapsedMs} ms. " +
                    "Results: {ResultCount}",
                    databaseStopwatch.ElapsedMilliseconds,
                    results.Count);

                // 13. Log retrieved results

                _logger.LogInformation(
                    "Retrieved RRF chunks: {ResultCount}",
                    results.Count);

                foreach (var result in results)
                {
                    _logger.LogDebug(
                        "Source: {DocumentName}, Chunk: {ChunkNumber}, " +
                        "Page: {PageNumber}, Distance: {Distance:F4}, " +
                        "KeywordScore: {KeywordScore:F4}, " +
                        "VectorRank: {VectorRank}, " +
                        "KeywordRank: {KeywordRank}, " +
                        "RrfScore: {RrfScore:F6}",

                        result.DocumentName,
                        result.ChunkNumber,
                        result.PageNumber,
                        result.Distance,
                        result.KeywordScore,
                        result.VectorRank,
                        result.KeywordRank,
                        result.RrfScore);
                }

                // 14. Total retrieval time

                totalStopwatch.Stop();

                _logger.LogInformation(
                    "RAG retrieval completed in {ElapsedMs} ms. " +
                    "Results: {ResultCount}",
                    totalStopwatch.ElapsedMilliseconds,
                    results.Count);

                return results;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();

                _logger.LogError(
                    ex,
                    "RAG retrieval failed for query '{Query}' after {ElapsedMs} ms.",
                    query,
                    totalStopwatch.ElapsedMilliseconds);

                throw;
            }
        }
        public async Task<DocumentInfo?> GetDocumentAsync(
            string documentName)
        {
           

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "GetDocument started. Document: {DocumentName}",
                documentName);

            // PostgreSQL connection

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

            // Execute query

            await using var reader =
                await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                stopwatch.Stop();

                _logger.LogInformation(
                    "GetDocument completed in {ElapsedMs} ms. " +
                    "Document not found: {DocumentName}",
                    stopwatch.ElapsedMilliseconds,
                    documentName);

                return null;
            }

            var document = new DocumentInfo
            {
                Id = reader.GetInt32(0),

                DocumentName =
                    reader.GetString(1),

                FileHash =
                    reader.GetString(2)
            };

            stopwatch.Stop();

            _logger.LogInformation(
                "GetDocument completed in {ElapsedMs} ms. " +
                "Document found: {DocumentName}",
                stopwatch.ElapsedMilliseconds,
                documentName);

            return document;
        }

        public async Task RegisterDocumentAsync(
            string documentName,
            string fileHash)
        {
            // Total timer

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "RegisterDocument started. Document: {DocumentName}",
                documentName);

            // PostgreSQL connection

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

            
            // Execute INSERT / UPDATE

            await cmd.ExecuteNonQueryAsync();

            stopwatch.Stop();

            _logger.LogInformation(
                "RegisterDocument completed in {ElapsedMs} ms. " +
                "Document: {DocumentName}",
                stopwatch.ElapsedMilliseconds,
                documentName);
        }

        public async Task DeleteDocumentChunksAsync(
            string documentName)
        {
            // Total timer

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "DeleteDocumentChunks started. Document: {DocumentName}",
                documentName);

            // PostgreSQL connection

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

            // Execute DELETE

            int deletedRows =
                await cmd.ExecuteNonQueryAsync();

            stopwatch.Stop();

            _logger.LogInformation(
                "DeleteDocumentChunks completed in {ElapsedMs} ms. " +
                "Document: {DocumentName}, DeletedRows: {DeletedRows}",
                stopwatch.ElapsedMilliseconds,
                documentName,
                deletedRows);
        }
    }
}