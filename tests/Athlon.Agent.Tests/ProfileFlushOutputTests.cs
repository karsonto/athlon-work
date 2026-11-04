using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Tests;

public sealed class ProfileFlushOutputTests
{
    private static readonly IReadOnlyList<ProfileKind> UserOnly = [ProfileKind.User];

    [Fact]
    public void Parse_returns_memory_section_and_user_document()
    {
        const string raw = """
## MEMORY
- prefers tabs

## USER
# USER

## Preferences
- Address as "Karson"
""";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, null);

        Assert.Equal("- prefers tabs", parsed.MemorySection);
        Assert.Equal(
            "# USER\n\n## Preferences\n- Address as \"Karson\"",
            parsed.ChangedDocuments[ProfileKind.User]);
    }

    [Fact]
    public void Parse_treats_unchanged_as_no_write()
    {
        const string raw = """
## MEMORY
NO_REPLY

## USER
UNCHANGED
""";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, null);

        Assert.Equal("NO_REPLY", parsed.MemorySection);
        Assert.Empty(parsed.ChangedDocuments);
    }

    [Fact]
    public void Parse_skips_identical_content()
    {
        const string existing = "# USER\n- prefers tabs";
        const string raw = """
## USER
# USER
- prefers tabs
""";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, new Dictionary<ProfileKind, string>
        {
            [ProfileKind.User] = existing
        });

        Assert.Empty(parsed.ChangedDocuments);
    }

    [Theory]
    [InlineData("## user")]
    [InlineData("## USER.md")]
    [InlineData("## User Profile")]
    [InlineData("## userprofile")]
    public void Parse_accepts_heading_variants(string heading)
    {
        var raw = $"{heading}\n- prefers Chinese";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, null);

        Assert.True(parsed.ChangedDocuments.ContainsKey(ProfileKind.User));
    }

    [Fact]
    public void Parse_falls_back_to_memory_when_no_heading_is_recognized()
    {
        const string raw = "- prefers tabs\n- uses dotnet";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, null);

        Assert.Equal(raw, parsed.MemorySection);
        Assert.Empty(parsed.ChangedDocuments);
    }

    [Fact]
    public void Parse_ignores_a_document_that_was_not_requested()
    {
        const string raw = """
## MEMORY
- a fact

## LESSONS
- never use pnpm
""";

        var parsed = ProfileFlushOutputParser.Parse(raw, UserOnly, null);

        Assert.Equal("- a fact", parsed.MemorySection);
        Assert.Empty(parsed.ChangedDocuments);
    }

    [Fact]
    public void Parse_handles_both_documents_at_once()
    {
        const string raw = """
## MEMORY
- a fact

## USER
- address as Karson

## LESSONS
- tool file_write failed on read-only mount -> check permissions first
""";

        var parsed = ProfileFlushOutputParser.Parse(raw, ProfileKinds.All, null);

        Assert.Equal("- a fact", parsed.MemorySection);
        Assert.Equal(2, parsed.ChangedDocuments.Count);
        Assert.Contains("Karson", parsed.ChangedDocuments[ProfileKind.User], StringComparison.Ordinal);
        Assert.Contains("read-only", parsed.ChangedDocuments[ProfileKind.Lessons], StringComparison.Ordinal);
    }
}
