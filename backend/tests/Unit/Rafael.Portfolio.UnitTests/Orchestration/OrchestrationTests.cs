using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.UnitTests.Orchestration;

public sealed class OrchestrationTests
{
    [Fact]
    public async Task Renders_only_whole_canonical_claims_and_selected_citations_then_settles_actual_usage()
    {
        var h = new OrchestrationHarness();
        var answer = await h.Run();
        Assert.NotNull(answer);
        Assert.Equal("Here is what Rafael's public profile documents:\n\n- " + OrchestrationHarness.Statement, answer.Answer);
        Assert.Equal("grounded", answer.GroundingStatus);
        Assert.Equal(["claim-1"], Assert.Single(answer.Citations).Claims);
        Assert.Equal(["reserve", "begin", "complete", "settle"], h.Ledger.Calls);
        Assert.Equal(ReservationState.Settled, h.Record.State);
        Assert.False(h.Record.CallInFlight);
        Assert.NotNull(h.Record.ExpiresAt);
        Assert.Equal(14, h.Real.DayCharged(ModelControlFixtures.Noon));
        Assert.Equal(0, h.Real.ActivePermits);
        Assert.Same(h.Counter.Request, h.Provider.Request);
        Assert.Contains("additionalProperties", h.Counter.Request!.OutputSchema);
        Assert.Contains("does not confirm a current role", h.Counter.Request.UserMessage);
    }

    public static IEnumerable<object[]> BadOutputs()
    {
        foreach (var text in new[]
        {
            "Rafael is an AI Engineer at StaffHub.",
            "AI Engineer role at Expinn", "null", "[]", "{}", "{", new string('x', 257),
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"],\"text\":\"Rafael is an AI Engineer at StaffHub.\"}",
            "{\"v\":1,\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"],\"claims\":[\"C2\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\",\"C1\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C99\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"c1\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"\\u00431\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[]}",
            "{\"v\":1,\"outcome\":\"insufficient\",\"claims\":[\"C1\"]}",
            "{\"v\":2,\"outcome\":\"answer\",\"claims\":[\"C1\"]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[[[[\"C1\"]]]]}",
            "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\",\"C2\",\"C3\",\"C4\"]}",
            OrchestrationHarness.Valid + " trailing", OrchestrationHarness.Valid + "{}"
        }) yield return [text];
    }

    [Theory]
    [MemberData(nameof(BadOutputs))]
    public async Task Invalid_output_never_reaches_the_visitor_but_known_usage_is_settled(string text)
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true, text, 100, 10));
        Assert.Null(await h.Run());
        Assert.Equal(ReservationState.Settled, h.Record.State);
        Assert.Equal(14, h.Record.ChargedMicroUsd);
        Assert.Equal(0, h.Real.ActivePermits);
    }

    [Fact]
    public async Task Insufficient_uses_A004_copy_without_citations()
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true,
            "{\"v\":1,\"outcome\":\"insufficient\",\"claims\":[]}", 100, 10));
        var answer = await h.Run();
        Assert.Equal(AssistantGroundingStatus.NotDocumented, answer!.GroundingStatus);
        Assert.Empty(answer.Citations);
        Assert.Contains("don't have documented evidence", answer.Answer);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("pending")]
    [InlineData("private")]
    [InlineData("no-claims")]
    [InlineData("pending-claim")]
    [InlineData("long-claim")]
    [InlineData("disabled")]
    public async Task Noneligible_turns_touch_no_counter_ledger_or_provider(string mode)
    {
        var h = new OrchestrationHarness();
        h.Chunks = mode switch
        {
            "none" => [],
            "pending" => [h.Chunks[0] with { EvidenceStatus = "pending" }],
            "private" => [h.Chunks[0] with { Visibility = "private" }],
            "no-claims" => [h.Chunks[0] with { ApprovedClaims = null }],
            "pending-claim" => [h.Chunks[0] with { ApprovedClaims = [h.Chunks[0].ApprovedClaims![0] with { Status = "pending" }] }],
            "long-claim" => [h.Chunks[0] with { ApprovedClaims = [h.Chunks[0].ApprovedClaims![0] with { Statement = new string('x', 401) }] }],
            _ => h.Chunks
        };
        h.Provider.IsAvailable = mode != "disabled";
        Assert.Null(await h.Run());
        Assert.Empty(h.Ledger.Calls);
        Assert.Equal(0, h.Counter.Calls);
        Assert.Equal(0, h.Provider.Calls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(6001, true)]
    [InlineData(100, false)]
    public async Task Invalid_or_unverified_counts_cancel_without_dispatch(int tokens, bool verified)
    {
        var h = new OrchestrationHarness();
        h.Counter.Count = (_, _) => ValueTask.FromResult(new ModelRequestTokenCount(tokens,
            verified ? ModelTokenCountBasis.ProviderVerifiedUpperBound : ModelTokenCountBasis.Unverified));
        Assert.Null(await h.Run());
        Assert.Equal(["reserve", "cancel"], h.Ledger.Calls);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(ReservationState.Cancelled, h.Record.State);
        Assert.Equal(0, h.Record.ChargedMicroUsd);
    }

    [Theory]
    [InlineData("漢字漢字")]
    [InlineData("😀😀")]
    [InlineData("éé")]
    public async Task Unicode_count_comes_from_the_complete_request_not_UTF16_length(string question)
    {
        var h = new OrchestrationHarness();
        h.Counter.Count = (request, _) =>
        {
            Assert.NotEqual(question.Length, System.Text.Encoding.UTF8.GetByteCount(question));
            Assert.Contains("question", request.UserMessage);
            Assert.NotEmpty(request.SystemPrompt);
            Assert.NotEmpty(request.OutputSchema);
            return ValueTask.FromResult(new ModelRequestTokenCount(6001, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        };
        Assert.Null(await h.Run(question: question));
        Assert.Equal(0, h.Provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_invalid_usage_falls_back_and_keeps_the_permit(bool invalid)
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true, OrchestrationHarness.Valid,
            invalid ? 0 : null, invalid ? 0 : null));
        Assert.Null(await h.Run());
        Assert.Equal(ReservationState.Uncertain, h.Record.State);
        Assert.True(h.Record.CallInFlight);
        Assert.Null(h.Record.ExpiresAt);
        Assert.Equal(ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
        Assert.Equal(1, h.Real.ActivePermits);
    }

    [Fact]
    public async Task Failed_provider_status_still_accounts_reported_usage_without_displaying_output()
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(false, OrchestrationHarness.Valid, 100, 10));
        Assert.Null(await h.Run());
        Assert.Equal(ReservationState.Settled, h.Record.State);
    }

    [Fact]
    public async Task Counter_underestimation_within_the_turn_allowance_still_accounts_actual_usage()
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true, OrchestrationHarness.Valid, 501, 10));
        Assert.NotNull(await h.Run());
        Assert.Equal(501, h.Record.InputTokensUsed);
        Assert.Equal(55, h.Record.ChargedMicroUsd);
    }
}
