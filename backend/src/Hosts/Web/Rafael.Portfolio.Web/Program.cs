using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Infrastructure;
using Rafael.Portfolio.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IPortfolioCatalog>(_ => EvidenceInventoryPortfolioCatalog.FromFile(
    builder.Configuration["EvidenceInventory:ManifestPath"]
    ?? EvidenceInventoryPortfolioCatalog.BundledManifestPath));
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

app.MapGroup("/v1").MapPortfolioEndpoints();

app.Run();

public partial class Program;
