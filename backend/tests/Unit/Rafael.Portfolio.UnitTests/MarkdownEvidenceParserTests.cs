using Rafael.Portfolio.Modules.Knowledge.Domain;
using Rafael.Portfolio.Modules.Knowledge.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class MarkdownEvidenceParserTests
{
    [Fact]
    public void ParseSections_returns_empty_when_markdown_is_empty()
    {
        var sections = MarkdownEvidenceParser.ParseSections("", []);
        Assert.Empty(sections);
    }

    [Fact]
    public void ParseSections_extracts_headings_and_content()
    {
        const string markdown = """
            # Document Title

            Some initial preamble.

            ## Architecture

            The architecture is modular.

            ## Claims Index

            | Claim ID | Status |
            |---|---|
            | claim-01 | pending |
            """;

        var sections = MarkdownEvidenceParser.ParseSections(markdown, []);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Overview", sections[0].Heading);
        Assert.Equal("overview", sections[0].Slug);
        Assert.Contains("Some initial preamble.", sections[0].Content);

        Assert.Equal("Architecture", sections[1].Heading);
        Assert.Equal("architecture", sections[1].Slug);
        Assert.Equal("The architecture is modular.", sections[1].Content);

        Assert.Equal("Claims Index", sections[2].Heading);
        Assert.Equal("claims-index", sections[2].Slug);
        Assert.Contains("claim-01", sections[2].Content);
    }

    [Fact]
    public void ParseSections_associates_claims_matching_anchor_slug()
    {
        const string markdown = """
            ## Focus Areas

            Key focus areas include agents.

            ## Engineering Principles

            Principles include evidence grounding.
            """;

        var claims = new List<EvidenceClaim>
        {
            new("claim-01", "Focus areas claim", "pending", "docs/evidence/profile.md#focus-areas"),
            new("claim-02", "Principles claim", "pending", "docs/evidence/profile.md#engineering-principles"),
            new("claim-03", "Other claim", "pending", "docs/evidence/profile.md#non-existent")
        };

        var sections = MarkdownEvidenceParser.ParseSections(markdown, claims);

        Assert.Equal(2, sections.Count);

        var focusSection = Assert.Single(sections, s => s.Slug == "focus-areas");
        var matchedFocusClaim = Assert.Single(focusSection.Claims);
        Assert.Equal("claim-01", matchedFocusClaim.ClaimId);

        var principlesSection = Assert.Single(sections, s => s.Slug == "engineering-principles");
        var matchedPrinciplesClaim = Assert.Single(principlesSection.Claims);
        Assert.Equal("claim-02", matchedPrinciplesClaim.ClaimId);
    }

    [Theory]
    [InlineData("Focus areas", "focus-areas")]
    [InlineData("Engineering principles", "engineering-principles")]
    [InlineData("Matching engine", "matching-engine")]
    [InlineData("Status notice", "status-notice")]
    [InlineData("Claims index", "claims-index")]
    [InlineData("  Multi - Word --- Heading!  ", "multi-word-heading")]
    public void Slugify_produces_consistent_slugs(string input, string expected)
    {
        var actual = MarkdownEvidenceParser.Slugify(input);
        Assert.Equal(expected, actual);
    }
}
