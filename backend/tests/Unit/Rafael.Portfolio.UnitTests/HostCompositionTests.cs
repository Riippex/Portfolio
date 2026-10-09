using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Web.Adapters;

namespace Rafael.Portfolio.UnitTests;

/// <summary>
/// Starts the real host (<c>Program.cs</c>), not a container rebuilt by the test, so a registration
/// that the host forgets is caught here and not only by the published artifact. Development turns
/// on service-provider validation, which is what makes an unresolvable dependency stop the host
/// from starting, exactly as it did when the evidence adapter was left unregistered.
/// </summary>
public sealed class HostCompositionTests
{
    private static WebApplicationFactory<Program> CreateHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Portfolio:Stage", "local");
        });

    [Fact]
    public async Task The_host_starts_and_resolves_the_assistant_dependencies_with_the_model_disabled()
    {
        await using var host = CreateHost();

        var services = host.Services; // building the host validates every registration

        Assert.IsType<KnowledgeAssistantEvidenceAdapter>(services.GetRequiredService<IAssistantEvidenceAdapter>());
        Assert.IsType<DisabledModelProvider>(services.GetRequiredService<IModelProvider>());
        Assert.IsType<ModelGroundedSynthesizer>(services.GetRequiredService<IAssistantSynthesizer>());
        Assert.IsType<AssistantService>(services.GetRequiredService<IAssistantService>());
        Assert.IsType<UnavailableModelControlLedger>(services.GetRequiredService<IModelControlLedger>());
    }

    [Fact]
    public async Task The_unconfigured_host_denies_paid_work_and_never_grants_an_allowance()
    {
        await using var host = CreateHost();
        var ledger = host.Services.GetRequiredService<IModelControlLedger>();

        var reservation = await ledger.TryReserveAsync("res_00000001");

        Assert.False(reservation.Success);
    }

    [Fact]
    public async Task The_running_host_answers_a_chat_request_from_evidence_with_the_model_disabled()
    {
        await using var host = CreateHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/assistant/chat",
            new { message = "Tell me about autonomous agents" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
