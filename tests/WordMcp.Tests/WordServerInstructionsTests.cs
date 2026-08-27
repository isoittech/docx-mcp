using WordMcp.Tools;

namespace WordMcp.Tests;

public sealed class WordServerInstructionsTests
{
    [Theory]
    [InlineData("word_start_document")]
    [InlineData("word_add_sections_to_draft")]
    [InlineData("word_finish_document")]
    [InlineData("word_wait_for_job")]
    [InlineData("word_get_preview_images")]
    [InlineData("preview_table_text_missing")]
    [InlineData("word_refine_document_section")]
    [InlineData("word_insert_document_sections")]
    public void BuildContainsRequiredWorkflowTool(string tool)
    {
        using var environment = new TestEnvironment();

        var instructions = WordServerInstructions.Build(environment.Options.Value);

        Assert.Contains(tool, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRequiresOpaqueTargetsBoundedWaitAndFullVisualReview()
    {
        using var environment = new TestEnvironment();

        var instructions = WordServerInstructions.Build(environment.Options.Value);

        Assert.Contains("Never invent", instructions, StringComparison.Ordinal);
        Assert.Contains("source SHA-256", instructions, StringComparison.Ordinal);
        Assert.Contains("every page", instructions, StringComparison.Ordinal);
        Assert.Contains("one to four", instructions, StringComparison.Ordinal);
        Assert.Contains("two rounds", instructions, StringComparison.Ordinal);
        Assert.Contains("rapidly polling", instructions, StringComparison.Ordinal);
        Assert.Contains("reflow", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.section_keys", instructions, StringComparison.Ordinal);
        Assert.Contains("rendered automatically as Heading 1", instructions, StringComparison.Ordinal);
        Assert.Contains("trusted current-message attachment header", instructions, StringComparison.Ordinal);
        Assert.Contains("Provider-extracted attachment text", instructions, StringComparison.Ordinal);
        Assert.Contains("more than one supported document", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInjectsOptionalDeploymentNoticeWithoutProvidingDefault()
    {
        using var environment = new TestEnvironment();

        var withoutNotice = WordServerInstructions.Build(environment.Options.Value);

        Assert.DoesNotContain("Deployment notice:", withoutNotice, StringComparison.Ordinal);
    }
}
