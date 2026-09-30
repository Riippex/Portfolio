using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Modules.Knowledge.Application;
using Rafael.Portfolio.Modules.Knowledge.Infrastructure;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Infrastructure;
using Rafael.Portfolio.Web.Adapters;
using Rafael.Portfolio.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IPortfolioCatalog>(_ => EvidenceInventoryPortfolioCatalog.FromFile(
    builder.Configuration["EvidenceInventory:ManifestPath"]
    ?? EvidenceInventoryPortfolioCatalog.BundledManifestPath));
builder.Services.AddSingleton<IEvidenceSource>(_ => FileSystemEvidenceSource.FromManifestFile(
    builder.Configuration["EvidenceInventory:ManifestPath"]
    ?? FileSystemEvidenceSource.BundledManifestPath));
builder.Services.AddSingleton<IEvidenceRetriever>(sp =>
    new InMemoryLexicalEvidenceRetriever(sp.GetRequiredService<IEvidenceSource>()));
builder.Services.AddSingleton<IAssistantEvidenceAdapter, KnowledgeAssistantEvidenceAdapter>();
builder.Services.AddSingleton<IAssistantSynthesizer, DeterministicGroundedSynthesizer>();
builder.Services.AddSingleton<IAssistantService, AssistantService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

var v1 = app.MapGroup("/v1");
v1.MapPortfolioEndpoints();
v1.MapKnowledgeEndpoints();
v1.MapAssistantEndpoints();

app.Run();

public partial class Program;
