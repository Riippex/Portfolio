using Rafael.Portfolio.Modules.Portfolio;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IPortfolioCatalog, PortfolioCatalog>();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

var api = app.MapGroup("/v1");

api.MapGet("/profile", (IPortfolioCatalog catalog) => Results.Ok(catalog.GetProfile()))
    .WithName("GetProfile");

api.MapGet("/projects", (IPortfolioCatalog catalog) => Results.Ok(catalog.GetProjects()))
    .WithName("GetProjects");

app.Run();

public partial class Program;
