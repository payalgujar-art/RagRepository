using Microsoft.AspNetCore.Mvc;
using RagApplication.Services;

namespace RagApplication.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RagController(RagService ragService) : ControllerBase
    {
        private readonly RagService _ragService = ragService;

        // GET: api/Rag/ask?query=What is RAG?
        [HttpGet("ask")]
        public async Task<IActionResult> Ask(
            [FromQuery] string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest("Query parameter is required.");
            }

            var response = await _ragService.GetAnswerAsync(query);

            return Ok(new
            {
                query,
                response
            });
        }
    }
}