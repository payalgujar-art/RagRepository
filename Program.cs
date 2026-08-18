using RagApplication.Interfaces;
using RagApplication.Services;
using RagApplication;
using RagApplication.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var configuration = builder.Configuration;

var connectionString = configuration.GetConnectionString("RagDatabase");

if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException(
        "RagDatabase connection string is missing.");
}

builder.Services.AddScoped<IChunkingService, ChunkingService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
builder.Services.AddScoped<IPdfTextExtractor, PdfTextExtractor>();

// Ollama embedding generator
builder.Services.AddSingleton<IEmbeddingGenerator>(sp =>
    new OllamaEmbeddingGenerator(
        new Uri("http://localhost:11434"),
        "nomic-embed-text"
    ));

// Repository
builder.Services.AddSingleton<TextRepository>(sp =>
    new TextRepository(
        connectionString,
        sp.GetRequiredService<IEmbeddingGenerator>()
    ));


builder.Services.AddSingleton<RagService>(sp =>
    new RagService(
        sp.GetRequiredService<TextRepository>(),
        new Uri("http://localhost:11434"),
        "mistral"
    ));
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularPolicy", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var ingestionService =
        scope.ServiceProvider
            .GetRequiredService<IDocumentIngestionService>();

    await ingestionService.IngestAsync();
}
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AngularPolicy");

app.UseAuthorization();

app.MapControllers();

app.Run();