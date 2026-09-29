using Rafael.Portfolio.Modules.Knowledge.Domain;
using Rafael.Portfolio.Modules.Knowledge.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class EvidenceIngestionTests
{
    [Fact]
    public void Canonical_evidence_inventory_ingests_cleanly_from_repository()
    {
        var manifestPath = TestRepositoryRoot.EvidenceInventoryManifestPath;
        var source = FileSystemEvidenceSource.FromManifestFile(manifestPath);

        var inventory = source.GetInventory();
        Assert.NotNull(inventory);
        Assert.False(string.IsNullOrWhiteSpace(inventory.Version));

        var allDocuments = source.GetAllEvidence();
        Assert.Equal(4, allDocuments.Count);

        Assert.All(allDocuments, doc =>
        {
            Assert.Equal(EvidenceVisibility.Public, doc.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(doc.RawMarkdown));
            Assert.NotEmpty(doc.Sections);
            Assert.True(EvidenceStatus.IsValid(doc.Item.EvidenceStatus));
        });
    }

    [Fact]
    public void Canonical_evidence_items_have_resolved_claim_citations()
    {
        var manifestPath = TestRepositoryRoot.EvidenceInventoryManifestPath;
        var source = FileSystemEvidenceSource.FromManifestFile(manifestPath);

        var vextis = source.GetEvidenceBySlug("vextis");
        Assert.NotNull(vextis);
        var archSection = Assert.Single(vextis.Sections, s => s.Slug == "architecture");
        Assert.Contains(archSection.Claims, c => c.ClaimId == "claim-vextis-01");

        var kinetiq = source.GetEvidenceBySlug("kinetiq-v");
        Assert.NotNull(kinetiq);
        var pipelineSection = Assert.Single(kinetiq.Sections, s => s.Slug == "pipeline");
        Assert.Contains(pipelineSection.Claims, c => c.ClaimId == "claim-kinetiq-v-01");

        var jobty = source.GetEvidenceBySlug("jobty");
        Assert.NotNull(jobty);
        var matchingSection = Assert.Single(jobty.Sections, s => s.Slug == "matching-engine");
        Assert.Contains(matchingSection.Claims, c => c.ClaimId == "claim-jobty-01");

        var profile = source.GetEvidenceBySlug("profile");
        Assert.NotNull(profile);
        var focusSection = Assert.Single(profile.Sections, s => s.Slug == "focus-areas");
        Assert.Contains(focusSection.Claims, c => c.ClaimId == "claim-profile-01");
        var principlesSection = Assert.Single(profile.Sections, s => s.Slug == "engineering-principles");
        Assert.Contains(principlesSection.Claims, c => c.ClaimId == "claim-profile-02");
    }

    [Fact]
    public void GetEvidenceBySlug_and_GetEvidenceById_handle_known_and_unknown_keys()
    {
        var manifestPath = TestRepositoryRoot.EvidenceInventoryManifestPath;
        var source = FileSystemEvidenceSource.FromManifestFile(manifestPath);

        var bySlug = source.GetEvidenceBySlug("vextis");
        var byId = source.GetEvidenceById("evidence-project-vextis");

        Assert.NotNull(bySlug);
        Assert.NotNull(byId);
        Assert.Same(bySlug, byId);

        Assert.Null(source.GetEvidenceBySlug("non-existent-slug"));
        Assert.Null(source.GetEvidenceById("non-existent-id"));
    }

    [Fact]
    public void Ingestion_fails_fast_when_referenced_document_is_missing()
    {
        var inventory = new PublicEvidenceInventory(
            "2026.09",
            new DateOnly(2026, 9, 29),
            [
                new PublicEvidenceItem(
                    "evidence-test",
                    "test",
                    "project",
                    "Test Project",
                    "Summary",
                    "2026.09",
                    EvidenceStatus.Pending,
                    null,
                    "docs/evidence/missing.md",
                    new DateOnly(2026, 9, 29),
                    [])
            ]);

        var documents = new Dictionary<string, string>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(inventory, documents));

        Assert.Contains("missing.md", ex.Message);
    }

    [Fact]
    public void Ingestion_fails_fast_on_invalid_evidence_status()
    {
        var inventory = new PublicEvidenceInventory(
            "2026.09",
            new DateOnly(2026, 9, 29),
            [
                new PublicEvidenceItem(
                    "evidence-test",
                    "test",
                    "project",
                    "Test Project",
                    "Summary",
                    "2026.09",
                    "unverified", // invalid status
                    null,
                    "docs/evidence/test.md",
                    new DateOnly(2026, 9, 29),
                    [])
            ]);

        var documents = new Dictionary<string, string>
        {
            ["docs/evidence/test.md"] = "# Test\n\n## Section\n\nContent"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(inventory, documents));

        Assert.Contains("invalid status", ex.Message);
    }

    [Fact]
    public void Ingestion_fails_fast_when_claim_anchor_does_not_exist_in_document()
    {
        var inventory = new PublicEvidenceInventory(
            "2026.09",
            new DateOnly(2026, 9, 29),
            [
                new PublicEvidenceItem(
                    "evidence-test",
                    "test",
                    "project",
                    "Test Project",
                    "Summary",
                    "2026.09",
                    EvidenceStatus.Pending,
                    null,
                    "docs/evidence/test.md",
                    new DateOnly(2026, 9, 29),
                    [
                        new EvidenceClaim(
                            "claim-01",
                            "Statement",
                            EvidenceStatus.Pending,
                            "docs/evidence/test.md#non-existent-section")
                    ])
            ]);

        var documents = new Dictionary<string, string>
        {
            ["docs/evidence/test.md"] = "# Test\n\n## Actual Section\n\nContent"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(inventory, documents));

        Assert.Contains("does not match any section", ex.Message);
    }
}
