using System.Windows.Documents;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class ComposerMentionDocumentTests
{
    [Fact]
    public void ParseMentions_captures_at_relative_path_with_trailing_space()
    {
        var spans = ComposerMentionDocument.ParseMentions("see @src/Foo.cs more");

        var span = Assert.Single(spans);
        Assert.Equal("@src/Foo.cs ", span.InsertText);
        Assert.Equal("Foo.cs", span.DisplayName);
        Assert.Equal("src/Foo.cs", span.RelativePath);
        Assert.Equal(ComposerMentionKind.File, span.Kind);
        Assert.Equal(WorkspaceFileIconKind.CSharp, span.IconKind);
        Assert.Equal(4, span.Start);
    }

    [Fact]
    public void ParseMentions_captures_folder_with_trailing_slash()
    {
        var spans = ComposerMentionDocument.ParseMentions("@src/ ");

        var span = Assert.Single(spans);
        Assert.Equal("@src/ ", span.InsertText);
        Assert.Equal("src", span.DisplayName);
        Assert.Equal(ComposerMentionKind.File, span.Kind);
        Assert.Equal(WorkspaceFileIconKind.Folder, span.IconKind);
    }

    [Fact]
    public void ParseMentions_skips_excluded_active_query()
    {
        const string text = "see @rea";
        var spans = ComposerMentionDocument.ParseMentions(text, excludeStart: 4, excludeEnd: text.Length);

        Assert.Empty(spans);
    }

    [Fact]
    public void ParseMentions_captures_skill_token_with_trailing_space()
    {
        var spans = ComposerMentionDocument.ParseMentions("use //skill:demo ");

        var span = Assert.Single(spans);
        Assert.Equal(ComposerMentionKind.Skill, span.Kind);
        Assert.Equal("//skill:demo ", span.InsertText);
        Assert.Equal("demo", span.DisplayName);
        Assert.Equal("demo", span.RelativePath);
        Assert.Equal(4, span.Start);
    }

    [Fact]
    public void ParseMentions_captures_mcp_token()
    {
        var spans = ComposerMentionDocument.ParseMentions("use //mcp:demo-server here");

        var span = Assert.Single(spans);
        Assert.Equal(ComposerMentionKind.Mcp, span.Kind);
        Assert.Equal("//mcp:demo-server ", span.InsertText);
        Assert.Equal("demo-server", span.DisplayName);
        Assert.Equal(4, span.Start);
    }

    [Fact]
    public void ParseMentions_skips_excluded_skill_token()
    {
        const string text = "//skill:demo";
        var spans = ComposerMentionDocument.ParseMentions(text, excludeStart: 0, excludeEnd: text.Length);

        Assert.Empty(spans);
    }

    [Fact]
    public void ParseMentions_skips_slash_commands_and_emails()
    {
        Assert.Empty(ComposerMentionDocument.ParseMentions("/clear"));
        Assert.Empty(ComposerMentionDocument.ParseMentions("write user@example.com"));
        Assert.Empty(ComposerMentionDocument.ParseMentions("see//skill:embedded"));
    }

    [Fact]
    public void WorkspaceFileIconResolver_maps_cs_to_csharp()
    {
        var kind = WorkspaceFileIconResolver.Resolve("IConversationTranscriptWriter.cs", "src/IConversationTranscriptWriter.cs", isDirectory: false, isPlaceholder: false);
        Assert.Equal(WorkspaceFileIconKind.CSharp, kind);
    }
}

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class ComposerMentionDocumentStaTests
{
    [Fact]
    public void Hydrate_round_trips_chip_to_at_relative_path()
    {
        RunSta(() =>
        {
            var document = new FlowDocument();
            const string original = "use @src/Foo.cs please";
            ComposerMentionDocument.Hydrate(document, original);

            Assert.Equal(1, ComposerMentionDocument.CountChips(document));
            var serialized = ComposerMentionDocument.Serialize(document);
            Assert.Equal(original, serialized);
            Assert.Contains("@src/Foo.cs", serialized, StringComparison.Ordinal);

            ComposerMentionDocument.Hydrate(document, serialized);
            Assert.Equal(1, ComposerMentionDocument.CountChips(document));
            var chip = document.Blocks.OfType<Paragraph>().Single().Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<ComposerFileChip>()
                .Single();
            Assert.Equal("Foo.cs", chip.FileName);
            Assert.Equal("@src/Foo.cs ", chip.InsertText);
            Assert.Equal(ComposerMentionKind.File, chip.MentionKind);
        });
    }

    [Fact]
    public void Hydrate_round_trips_skill_and_mcp_chips_without_prefix_in_display()
    {
        RunSta(() =>
        {
            var document = new FlowDocument();
            const string original = "use //skill:demo and //mcp:server please";
            ComposerMentionDocument.Hydrate(document, original);

            Assert.Equal(2, ComposerMentionDocument.CountChips(document));
            var serialized = ComposerMentionDocument.Serialize(document);
            Assert.Equal(original, serialized);
            Assert.Contains("//skill:demo", serialized, StringComparison.Ordinal);
            Assert.Contains("//mcp:server", serialized, StringComparison.Ordinal);

            var chips = document.Blocks.OfType<Paragraph>().Single().Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<ComposerFileChip>()
                .ToArray();
            Assert.Equal(2, chips.Length);
            Assert.Equal(ComposerMentionKind.Skill, chips[0].MentionKind);
            Assert.Equal("demo", chips[0].FileName);
            Assert.Equal("//skill:demo ", chips[0].InsertText);
            Assert.DoesNotContain("//", chips[0].FileName, StringComparison.Ordinal);
            Assert.Equal(ComposerMentionKind.Mcp, chips[1].MentionKind);
            Assert.Equal("server", chips[1].FileName);
            Assert.Equal("//mcp:server ", chips[1].InsertText);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}
