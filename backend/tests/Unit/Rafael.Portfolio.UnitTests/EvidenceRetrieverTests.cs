using Rafael.Portfolio.Modules.Knowledge.Domain;
using Rafael.Portfolio.Modules.Knowledge.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class EvidenceRetrieverTests
{
    private static InMemoryLexicalEvidenceRetriever CreateRetriever()
    {
        var manifestPath = TestRepositoryRoot.EvidenceInventoryManifestPath;
        var source = FileSystemEvidenceSource.FromManifestFile(manifestPath);
        return new InMemoryLexicalEvidenceRetriever(source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Retrieve_returns_empty_when_query_is_empty_or_whitespace(string query)
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve(query);
        Assert.Empty(results);
    }

    [Fact]
    public void Retrieve_ranks_vextis_architecture_first_for_autonomous_agents_query()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("vextis autonomous agents architecture");

        Assert.NotEmpty(results);
        var top = results[0];

        Assert.Equal("vextis", top.Slug);
        Assert.Equal("architecture", top.SectionSlug);
        Assert.Equal(EvidenceVisibility.Public, top.Visibility);
        Assert.Equal("2026.09", top.Version);
        Assert.Equal(EvidenceStatus.Pending, top.EvidenceStatus);
        Assert.True(top.Score > 0);
        Assert.Contains(top.Claims, c => c.ClaimId == "claim-vextis-01");
        Assert.Contains("docs/evidence/projects/vextis.md#architecture", top.Citations);
    }

    [Fact]
    public void Retrieve_ranks_kinetiq_pipeline_first_for_computer_vision_stream_query()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("real-time computer vision stream processing");

        Assert.NotEmpty(results);
        var top = results[0];

        Assert.Equal("kinetiq-v", top.Slug);
        Assert.Equal("pipeline", top.SectionSlug);
        Assert.Equal(EvidenceVisibility.Public, top.Visibility);
        Assert.Contains(top.Claims, c => c.ClaimId == "claim-kinetiq-v-01");
        Assert.Contains("docs/evidence/projects/kinetiq-v.md#pipeline", top.Citations);
    }

    [Fact]
    public void Retrieve_ranks_jobty_matching_engine_first_for_vacancy_evaluation_query()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("job vacancy evaluation engine skills gaps");

        Assert.NotEmpty(results);
        var top = results[0];

        Assert.Equal("jobty", top.Slug);
        Assert.Equal("matching-engine", top.SectionSlug);
        Assert.Equal(EvidenceVisibility.Public, top.Visibility);
        Assert.Contains(top.Claims, c => c.ClaimId == "claim-jobty-01");
        Assert.Contains("docs/evidence/projects/jobty.md#matching-engine", top.Citations);
    }

    [Fact]
    public void Retrieve_ranks_profile_studies_first_for_university_query()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("studies at Universidad Manuela Beltran");

        Assert.NotEmpty(results);
        var top = results[0];

        Assert.Equal("profile", top.Slug);
        Assert.Equal("studies", top.SectionSlug);
        Assert.Equal(EvidenceVisibility.Public, top.Visibility);
        Assert.Contains(top.Claims, c => c.ClaimId == "claim-profile-04");
        Assert.Contains("docs/evidence/profile.md#studies", top.Citations);
    }

    [Fact]
    public void Retrieve_filters_by_slug_when_specified()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("autonomous agents", limit: 10, slugFilter: "jobty");

        Assert.All(results, chunk => Assert.Equal("jobty", chunk.Slug));
    }

    [Fact]
    public void Retrieve_respects_limit()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("evidence architecture systems", limit: 2);

        Assert.True(results.Count <= 2);
    }

    [Fact]
    public void Retrieve_returns_empty_when_no_terms_match()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("xylophone zebra quantum-superposition");

        Assert.Empty(results);
    }

    [Fact]
    public void Retrieve_rejects_oversized_query_text()
    {
        var retriever = CreateRetriever();
        var oversized = new string('a', RetrievalQuery.MaxQueryTextLength + 1);

        Assert.Throws<ArgumentException>(() => retriever.Retrieve(oversized));
    }

    [Theory]
    [InlineData("Invalid_Slug!!")]
    [InlineData("../escape")]
    [InlineData("")]
    public void Retrieve_rejects_invalid_slug_filter(string slug)
    {
        var retriever = CreateRetriever();

        Assert.Throws<ArgumentException>(() => retriever.Retrieve("agents", slugFilter: slug));
    }

    [Fact]
    public void Retrieve_rejects_oversized_slug_filter()
    {
        var retriever = CreateRetriever();
        var oversized = new string('a', RetrievalQuery.MaxSlugFilterLength + 1);

        Assert.Throws<ArgumentException>(() => retriever.Retrieve("agents", slugFilter: oversized));
    }

    [Fact]
    public void Retrieve_clamps_limit_between_one_and_fifty()
    {
        var retriever = CreateRetriever();

        var high = retriever.Retrieve("evidence architecture systems", limit: 500);
        Assert.InRange(high.Count, 1, 50);

        var low = retriever.Retrieve("evidence architecture systems", limit: 0);
        Assert.True(low.Count <= 1);
    }

    [Fact]
    public void All_retrieved_chunks_carry_required_metadata()
    {
        var retriever = CreateRetriever();
        var results = retriever.Retrieve("architecture pipeline engine systems principles", limit: 20);

        Assert.NotEmpty(results);
        Assert.All(results, chunk =>
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk.ChunkId));
            Assert.False(string.IsNullOrWhiteSpace(chunk.DocumentId));
            Assert.False(string.IsNullOrWhiteSpace(chunk.Slug));
            Assert.False(string.IsNullOrWhiteSpace(chunk.Title));
            Assert.False(string.IsNullOrWhiteSpace(chunk.SectionHeading));
            Assert.False(string.IsNullOrWhiteSpace(chunk.SectionSlug));
            Assert.False(string.IsNullOrWhiteSpace(chunk.Content));
            Assert.False(string.IsNullOrWhiteSpace(chunk.Version));
            Assert.Equal(EvidenceVisibility.Public, chunk.Visibility);
            Assert.True(EvidenceStatus.IsValid(chunk.EvidenceStatus));
            Assert.True(chunk.Score > 0);
            Assert.NotEmpty(chunk.Citations);
        });
    }
}
