using WordMcp.Rendering;

namespace WordMcp.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void ChildProcessEnvironmentIsMinimalAndDoesNotInheritApplicationSecrets()
    {
        var startInfo = ProcessRunner.CreateStartInfo(
            "/usr/bin/env",
            [],
            "/tmp/word-mcp-test-process",
            new Dictionary<string, string?> { ["SAL_USE_VCLPLUGIN"] = "gen" });

        Assert.Equal(
            ["HOME", "LANG", "LC_ALL", "PATH", "SAL_USE_VCLPLUGIN", "TMPDIR"],
            startInfo.Environment.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("WordMcp__SharedSecret", startInfo.Environment.Keys);
        Assert.DoesNotContain("WORD_MCP_RENDER_INTEGRATION", startInfo.Environment.Keys);
    }
}
