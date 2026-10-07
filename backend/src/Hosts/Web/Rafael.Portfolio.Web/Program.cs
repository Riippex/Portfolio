using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Infrastructure;
using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Infrastructure;
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
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAssistantSafetyEvaluator, AssistantSafetyEvaluator>();
builder.Services.AddSingleton<IAssistantRateLimiter>(_ => new InMemorySlidingWindowRateLimiter(10, TimeSpan.FromSeconds(60)));

var turnstileSecret = builder.Configuration["Turnstile:SecretKey"];
var proxyIdentitySecret = builder.Configuration["AssistantSecurity:ProxyIdentitySecret"];
var turnstileBypassAllowed = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test");

if (string.IsNullOrWhiteSpace(turnstileSecret) && !turnstileBypassAllowed)
{
    throw new InvalidOperationException(
        "Turnstile:SecretKey must be configured outside Development/Test environments. " +
        "Human verification fails closed rather than running unprotected.");
}

if (string.IsNullOrWhiteSpace(proxyIdentitySecret) && !turnstileBypassAllowed)
{
    throw new InvalidOperationException(
        "AssistantSecurity:ProxyIdentitySecret must be configured outside Development/Test environments. " +
        "Per-visitor rate limiting requires the signed proxy identity boundary.");
}

builder.Services.AddSingleton<ITurnstileValidator>(sp =>
    string.IsNullOrWhiteSpace(turnstileSecret)
        ? new DisabledTurnstileValidator()
        : new CloudflareTurnstileValidator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("turnstile"),
            turnstileSecret));
builder.Services.AddSingleton<IAssistantEvidenceAdapter, KnowledgeAssistantEvidenceAdapter>();
builder.Services.AddSingleton<IAssistantSynthesizer, DeterministicGroundedSynthesizer>();
builder.Services.AddSingleton<IAssistantService, AssistantService>();
builder.Services.AddSingleton<IJobMatchingEvidenceAdapter, KnowledgeJobMatchingEvidenceAdapter>();
builder.Services.AddSingleton<IJobDescriptionAnalyzer, DeterministicJobDescriptionAnalyzer>();
builder.Services.AddSingleton<IJobMatchingService, JobMatchingService>();

var contactOptions = builder.Configuration.GetSection(ContactOptions.SectionName).Get<ContactOptions>() ?? new ContactOptions();
contactOptions.Validate();
builder.Services.AddSingleton(contactOptions);

builder.Services.AddHttpClient("cloudflare-email");
builder.Logging.AddFilter("System.Net.Http.HttpClient.cloudflare-email", LogLevel.Warning);
builder.Services.AddSingleton<IContactRelay>(sp =>
    new CloudflareEmailRelay(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("cloudflare-email"),
        sp.GetRequiredService<ContactOptions>()));
builder.Services.AddSingleton<IContactRateLimiter>(_ => new InMemoryContactRateLimiter(3, TimeSpan.FromMinutes(10)));
builder.Services.AddSingleton<IContactService, ContactService>();

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
v1.MapJobMatchingEndpoints();
v1.MapContactEndpoints();

app.Run();

public partial class Program;
