using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RagApplication.Models;

namespace RagApplication.Services
{
    public class RagService(
        TextRepository retriever,
        string groqApiKey,
        ILogger<RagService> logger,
        string modelId = "openai/gpt-oss-20b")
    {
        private readonly TextRepository _textRepository = retriever;

        private readonly HttpClient _httpClient = new();

        private readonly string _groqApiKey = groqApiKey;

        private readonly ILogger<RagService> _logger = logger;

        private readonly string _modelId = modelId;

        public async Task<object> GetAnswerAsync(string query)
        {
            // =========================================================
            // TOTAL RAG REQUEST TIMER
            // =========================================================

            var totalStopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "RAG request started. Query: {Query}, Model: {Model}",
                query,
                _modelId);

            // =========================================================
            // 1. RETRIEVE RELEVANT CHUNKS
            // =========================================================

            var retrievalStopwatch = Stopwatch.StartNew();

            List<RetrievedChunk> chunks =
                await _textRepository.RetrieveRelevantChunks(
                    query,
                    topK: 5);

            retrievalStopwatch.Stop();

            _logger.LogInformation(
                "RAG retrieval completed in {ElapsedMs} ms. Chunks retrieved: {ChunkCount}",
                retrievalStopwatch.ElapsedMilliseconds,
                chunks.Count);

            // =========================================================
            // 2. NO RELEVANT CHUNKS
            // =========================================================

            if (chunks.Count == 0)
            {
                totalStopwatch.Stop();

                _logger.LogInformation(
                    "RAG request completed without relevant chunks in {ElapsedMs} ms.",
                    totalStopwatch.ElapsedMilliseconds);

                return new
                {
                    Query = query,

                    Answer =
                        "I don't have enough information in the provided documents.",

                    Citations = Array.Empty<object>()
                };
            }

            // =========================================================
            // 3. BUILD RAG CONTEXT
            // =========================================================

            var contextStopwatch = Stopwatch.StartNew();

            var contextBuilder = new StringBuilder();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                string sourceId = $"SOURCE_{i + 1}";

                contextBuilder.AppendLine(
                    $"[{sourceId}]");

                contextBuilder.AppendLine(
                    $"Document: {chunk.DocumentName}");

                contextBuilder.AppendLine(
                    $"Page: {(chunk.PageNumber?.ToString() ?? "N/A")}");

                contextBuilder.AppendLine(
                    $"Chunk: {chunk.ChunkNumber}");

                contextBuilder.AppendLine(
                    $"Content: {chunk.Content}");

                contextBuilder.AppendLine();
            }

            string combinedContext =
                contextBuilder.ToString();

            contextStopwatch.Stop();

            _logger.LogInformation(
                "RAG context building completed in {ElapsedMs} ms. Context length: {ContextLength} characters.",
                contextStopwatch.ElapsedMilliseconds,
                combinedContext.Length);

            // =========================================================
            // 4. CREATE GROQ REQUEST
            // =========================================================

            var requestStopwatch = Stopwatch.StartNew();

            /*
             * Keep the system prompt short.
             *
             * We only need:
             * - answer
             * - sourceIds
             *
             * This reduces unnecessary output tokens.
             */

            string systemPrompt = """
                You are a strict knowledge-base assistant.

                Answer ONLY from the provided context.

                Rules:
                - Do not use outside knowledge.
                - Do not invent information.
                - If the answer is not in the context, answer that there is not enough information.
                - Return ONLY valid JSON.
                - Do not use markdown.
                - sourceIds must contain only SOURCE IDs that support the answer.
                - Use SOURCE IDs exactly as provided.

                JSON format:
                {
                  "answer": "string",
                  "sourceIds": ["SOURCE_1"]
                }
                """;

            string userPrompt = $"""
                Context:
                {combinedContext}

                Question:
                {query}
                """;

            var requestBody = new
            {
                model = _modelId,

                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = systemPrompt
                    },
                    new
                    {
                        role = "user",
                        content = userPrompt
                    }
                },

                temperature = 0,

                /*
                 * 300 was too small in your previous request.
                 *
                 * The error was:
                 * "max completion tokens reached before generating
                 * a valid document"
                 *
                 * 500 gives the model enough room to finish JSON.
                 */
                max_completion_tokens = 500,

                response_format = new
                {
                    type = "json_object"
                }
            };

            requestStopwatch.Stop();

            _logger.LogInformation(
                "Groq LLM request creation completed in {ElapsedMs} ms.",
                requestStopwatch.ElapsedMilliseconds);

            // =========================================================
            // 5. CALL GROQ
            // =========================================================

            var groqStopwatch = Stopwatch.StartNew();

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.groq.com/openai/v1/chat/completions");

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    _groqApiKey);

            request.Content =
                new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

            HttpResponseMessage response;

            try
            {
                response =
                    await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                groqStopwatch.Stop();

                _logger.LogError(
                    ex,
                    "Groq request failed after {ElapsedMs} ms.",
                    groqStopwatch.ElapsedMilliseconds);

                totalStopwatch.Stop();

                return new
                {
                    Query = query,

                    Answer =
                        "Error: Unable to generate response.",

                    Citations = Array.Empty<object>()
                };
            }

            groqStopwatch.Stop();

            _logger.LogInformation(
                "Groq generation completed in {ElapsedMs} ms. StatusCode: {StatusCode}",
                groqStopwatch.ElapsedMilliseconds,
                (int)response.StatusCode);

            // =========================================================
            // 6. READ GROQ RESPONSE
            // =========================================================

            string responseJson =
                await response.Content.ReadAsStringAsync();

            _logger.LogDebug(
                "Groq raw response: {Response}",
                responseJson);

            // =========================================================
            // 7. HANDLE GROQ ERROR
            // =========================================================

            if (!response.IsSuccessStatusCode)
            {
                totalStopwatch.Stop();

                _logger.LogError(
                    "Groq request failed. StatusCode: {StatusCode}. Response: {Response}. Total RAG time: {ElapsedMs} ms.",
                    (int)response.StatusCode,
                    responseJson,
                    totalStopwatch.ElapsedMilliseconds);

                return new
                {
                    Query = query,

                    Answer =
                        "Error: Unable to generate response.",

                    Citations = Array.Empty<object>()
                };
            }

            // =========================================================
            // 8. DESERIALIZE GROQ RESPONSE
            // =========================================================

            var serializationOptions =
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

            var deserializeStopwatch =
                Stopwatch.StartNew();

            GroqCompletionResponse? completionResponse = null;

            try
            {
                completionResponse =
                    JsonSerializer.Deserialize<GroqCompletionResponse>(
                        responseJson,
                        serializationOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to deserialize Groq response.");

                totalStopwatch.Stop();

                return new
                {
                    Query = query,

                    Answer =
                        "Error: Invalid response received from Groq.",

                    Citations = Array.Empty<object>()
                };
            }

            string generatedAnswer =
                completionResponse?
                    .Choices?
                    .FirstOrDefault()?
                    .Message?
                    .Content
                ?? string.Empty;

            deserializeStopwatch.Stop();

            _logger.LogInformation(
                "Groq response deserialization completed in {ElapsedMs} ms.",
                deserializeStopwatch.ElapsedMilliseconds);

            _logger.LogDebug(
                "Generated LLM JSON: {GeneratedAnswer}",
                generatedAnswer);

            // =========================================================
            // 9. PARSE RAG JSON
            // =========================================================

            var parsingStopwatch =
                Stopwatch.StartNew();

            RagAnswer? ragAnswer = null;

            try
            {
                ragAnswer =
                    JsonSerializer.Deserialize<RagAnswer>(
                        generatedAnswer,
                        serializationOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Groq returned invalid JSON. Generated content: {GeneratedAnswer}",
                    generatedAnswer);

                ragAnswer = new RagAnswer
                {
                    Answer =
                        "I don't have enough information in the provided documents.",

                    SourceIds = []
                };
            }

            parsingStopwatch.Stop();

            _logger.LogInformation(
                "LLM JSON parsing completed in {ElapsedMs} ms.",
                parsingStopwatch.ElapsedMilliseconds);

            // =========================================================
            // 10. VALIDATE ANSWER
            // =========================================================

            string answer =
                string.IsNullOrWhiteSpace(ragAnswer?.Answer)
                    ? "I don't have enough information in the provided documents."
                    : ragAnswer.Answer;

            _logger.LogInformation(
                "Answer validation completed. Answer length: {AnswerLength}",
                answer.Length);

            // =========================================================
            // 11. MAP SOURCE IDs TO CHUNKS
            // =========================================================

            var citationStopwatch =
                Stopwatch.StartNew();

            var citations = new List<object>();

            if (ragAnswer?.SourceIds != null)
            {
                foreach (string sourceId in ragAnswer.SourceIds)
                {
                    if (!sourceId.StartsWith(
                            "SOURCE_",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Ignoring invalid source ID returned by LLM: {SourceId}",
                            sourceId);

                        continue;
                    }

                    string numberPart =
                        sourceId["SOURCE_".Length..];

                    if (!int.TryParse(
                            numberPart,
                            out int sourceNumber))
                    {
                        _logger.LogWarning(
                            "Unable to parse source ID: {SourceId}",
                            sourceId);

                        continue;
                    }

                    int index =
                        sourceNumber - 1;

                    if (index < 0 ||
                        index >= chunks.Count)
                    {
                        _logger.LogWarning(
                            "Source ID {SourceId} points outside retrieved chunks.",
                            sourceId);

                        continue;
                    }

                    var chunk = chunks[index];

                    citations.Add(new
                    {
                        SourceId = sourceId,

                        Document = chunk.DocumentName,

                        Page = chunk.PageNumber,

                        Chunk = chunk.ChunkNumber,

                        Distance = chunk.Distance
                    });
                }
            }

            citationStopwatch.Stop();

            _logger.LogInformation(
                "Citation mapping completed in {ElapsedMs} ms. Citations: {CitationCount}",
                citationStopwatch.ElapsedMilliseconds,
                citations.Count);

            // =========================================================
            // 12. TOTAL REQUEST TIME
            // =========================================================

            totalStopwatch.Stop();

            _logger.LogInformation(
                "RAG request completed in {ElapsedMs} ms.",
                totalStopwatch.ElapsedMilliseconds);

            // =========================================================
            // 13. RETURN RESPONSE
            // =========================================================

            return new
            {
                Query = query,

                Answer = answer,

                Citations = citations
            };
        }
    }
}