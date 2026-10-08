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
        var contextSection = Assert.Single(profile.Sections, s => s.Slug == "professional-context");
        Assert.Contains(contextSection.Claims, c => c.ClaimId == "claim-profile-02");
        var principlesSection = Assert.Single(profile.Sections, s => s.Slug == "engineering-principles");
        Assert.Contains(principlesSection.Claims, c => c.ClaimId == "claim-profile-03");
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

    [Fact]
    public void FromManifestFile_rejects_manifest_violating_the_canonical_schema()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "test.md"), "# Test\n\n## Section\n\nContent");
            var manifestPath = Path.Combine(root, "inventory.json");
            File.WriteAllText(manifestPath, ManifestJson("docs/evidence/test.md", evidenceStatus: "corrupted"));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                FileSystemEvidenceSource.FromManifestFile(manifestPath));

            Assert.Contains("canonical JSON Schema", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FromManifestFile_rejects_document_paths_escaping_the_evidence_directory()
    {
        var root = CreateTempDirectory();
        try
        {
            var evidenceDir = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidenceDir);

            // The target file exists outside the evidence directory; the
            // rejection must come from containment, not from a missing file.
            File.WriteAllText(Path.Combine(root, "secret.md"), "# Secret");

            var manifestPath = Path.Combine(evidenceDir, "inventory.json");
            File.WriteAllText(manifestPath, ManifestJson("docs/evidence/../secret.md"));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                FileSystemEvidenceSource.FromManifestFile(manifestPath));

            Assert.Contains("escapes the evidence directory", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FromManifestFile_rejects_rooted_document_paths()
    {
        var root = CreateTempDirectory();
        try
        {
            var documentPath = Path.Combine(root, "test.md");
            File.WriteAllText(documentPath, "# Test\n\n## Section\n\nContent");

            var manifestPath = Path.Combine(root, "inventory.json");
            File.WriteAllText(manifestPath, ManifestJson(documentPath));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                FileSystemEvidenceSource.FromManifestFile(manifestPath));

            Assert.Contains("must be a relative path", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FromManifestFile_handles_case_variant_sibling_paths_per_filesystem_semantics()
    {
        var root = CreateTempDirectory();
        try
        {
            var evidenceDir = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidenceDir);

            // On Windows this is the same directory as evidenceDir; on
            // Linux/macOS it is a distinct sibling that must stay out of reach.
            var siblingDir = Path.Combine(root, "Evidence");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "secret.md"), "# Secret");

            var manifestPath = Path.Combine(evidenceDir, "inventory.json");
            File.WriteAllText(manifestPath, ManifestJson("docs/evidence/../Evidence/secret.md"));

            if (OperatingSystem.IsWindows())
            {
                var source = FileSystemEvidenceSource.FromManifestFile(manifestPath);
                Assert.Single(source.GetAllEvidence());
            }
            else
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    FileSystemEvidenceSource.FromManifestFile(manifestPath));

                Assert.Contains("escapes the evidence directory", ex.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ingestion_fails_fast_on_empty_claim_citation(string citation)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(InventoryWithClaim(citation), TestDocuments()));

        Assert.Contains("must declare a citation", ex.Message);
    }

    [Fact]
    public void Ingestion_fails_fast_when_claim_citation_has_no_anchor()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(InventoryWithClaim("docs/evidence/test.md"), TestDocuments()));

        Assert.Contains("must include a non-empty section anchor", ex.Message);
    }

    [Fact]
    public void Ingestion_fails_fast_on_cross_document_claim_citation()
    {
        // The anchor "section" exists in the item's own document, but the
        // citation targets a different document and must still be rejected.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FileSystemEvidenceSource(
                InventoryWithClaim("docs/evidence/other.md#section"), TestDocuments()));

        Assert.Contains("must target the item's own canonical document", ex.Message);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"evidence-ingestion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ManifestJson(string documentPath, string evidenceStatus = "pending") =>
        $$"""
        {
          "version": "2026.09",
          "lastUpdated": "2026-09-29",
          "items": [
            {
              "id": "evidence-test",
              "slug": "test",
              "kind": "project",
              "title": "Test Project",
              "summary": "Summary",
              "version": "2026.09",
              "evidenceStatus": "{{evidenceStatus}}",
              "sourceUrl": null,
              "documentPath": "{{documentPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}}",
              "lastReviewed": "2026-09-29",
              "claims": []
            }
          ]
        }
        """;

    private static PublicEvidenceInventory InventoryWithClaim(string citation) =>
        new(
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
                    [new EvidenceClaim("claim-01", "Statement", EvidenceStatus.Pending, citation)])
            ]);

    private static Dictionary<string, string> TestDocuments() =>
        new()
        {
            ["docs/evidence/test.md"] = "# Test\n\n## Section\n\nContent"
        };
}
