using System.Text;
using System.Text.Json;
using RagApplication.Models;

namespace RagApplication.Services
{
    public class RagService(
        TextRepository retriever,
        Uri ollamaUrl,
        string modelId = "mistral")
    {
        private readonly TextRepository _textRepository = retriever;

        private readonly HttpClient _httpClient = new();

        private readonly Uri _ollamaUrl = ollamaUrl;

        private readonly string _modelId = modelId;

        public async Task<object> GetAnswerAsync(string query)
        {
            // -----------------------------------------
            // 1. Retrieve relevant chunks
            // -----------------------------------------

            List<RetrievedChunk> chunks =
                await _textRepository.RetrieveRelevantChunks(
                    query,
                    topK: 5);

            // -----------------------------------------
            // 2. No relevant chunks found
            // -----------------------------------------

            if (chunks.Count == 0)
            {
                return new
                {
                    Query = query,

                    Answer =
                        "I don't have enough information in the provided documents.",

                    Citations = Array.Empty<object>()
                };
            }

            // -----------------------------------------
            // 3. Build context with SOURCE IDs
            // -----------------------------------------

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

            // -----------------------------------------
            // 4. Create LLM request
            // -----------------------------------------

            var requestBody = new
            {
                model = _modelId,

                format = "json",

                prompt = $"""
        You are a strict knowledge-base assistant.

        Answer the user's question ONLY using the provided context.

        Rules:
        1. Do not use outside knowledge.
        2. Do not invent or assume information.
        3. If the answer is not present in the context,
           return an empty answer and an empty sourceIds array.
        4. Return ONLY valid JSON.
        5. Do not use markdown.
        6. Do not add explanations outside the JSON.
        7. sourceIds must contain ONLY SOURCE IDs that actually
           support your answer.
        8. Never create or modify a SOURCE ID.
        9. Use the exact SOURCE IDs provided in the context.

        The JSON must contain:
        - answer: the answer to the user's question
        - sourceIds: an array containing the SOURCE IDs that support the answer

        Context:
        {combinedContext}

        Question:
        {query}
        """,

                stream = false
            };
            // -----------------------------------------
            // 5. Call Ollama
            // -----------------------------------------

            var response =
                await _httpClient.PostAsync(
                    new Uri(_ollamaUrl, "/api/generate"),
                    new StringContent(
                        JsonSerializer.Serialize(requestBody),
                        Encoding.UTF8,
                        "application/json"));

            // -----------------------------------------
            // 6. Handle Ollama error
            // -----------------------------------------

            if (!response.IsSuccessStatusCode)
            {
                return new
                {
                    Query = query,

                    Answer =
                        "Error: Unable to generate response.",

                    Citations = Array.Empty<object>()
                };
            }

            // -----------------------------------------
            // 7. Read Ollama response
            // -----------------------------------------

            string responseJson =
                await response.Content.ReadAsStringAsync();

            var serializationOptions =
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

            var completionResponse =
                JsonSerializer.Deserialize<OllamaCompletionResponse>(
                    responseJson,
                    serializationOptions);

            string generatedAnswer =
                completionResponse?.Response ?? string.Empty;

            // -----------------------------------------
            // 8. Parse structured JSON from LLM
            // -----------------------------------------

            RagAnswer? ragAnswer = null;

            try
            {
                ragAnswer =
                    JsonSerializer.Deserialize<RagAnswer>(
                        generatedAnswer,
                        serializationOptions);
            }
            catch (JsonException)
            {
                // If LLM returned invalid JSON
                ragAnswer = new RagAnswer
                {
                    Answer =
                        "I don't have enough information in the provided documents.",

                    SourceIds = []
                };
            }

            // -----------------------------------------
            // 9. Validate answer
            // -----------------------------------------

            string answer =
                string.IsNullOrWhiteSpace(ragAnswer?.Answer)
                    ? "I don't have enough information in the provided documents."
                    : ragAnswer.Answer;

            // -----------------------------------------
            // 10. Map SOURCE IDs to actual chunks
            // -----------------------------------------

            var citations = new List<object>();

            if (ragAnswer?.SourceIds != null)
            {
                foreach (string sourceId in ragAnswer.SourceIds)
                {
                    if (!sourceId.StartsWith(
                            "SOURCE_",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string numberPart =
                        sourceId["SOURCE_".Length..];

                    if (!int.TryParse(
                            numberPart,
                            out int sourceNumber))
                    {
                        continue;
                    }

                    int index =
                        sourceNumber - 1;

                    // Make sure the LLM cannot reference
                    // a source that doesn't exist
                    if (index < 0 ||
                        index >= chunks.Count)
                    {
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

            // -----------------------------------------
            // 11. Return final response
            // -----------------------------------------

            return new
            {
                Query = query,

                Answer = answer,

                Citations = citations
            };
        }
    }
}