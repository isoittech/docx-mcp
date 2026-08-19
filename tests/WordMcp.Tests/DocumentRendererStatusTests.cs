using WordMcp.Rendering;

namespace WordMcp.Tests;

public sealed class DocumentRendererStatusTests
{
    [Theory]
    [InlineData(1, 2, true, 2, 2, 2, 2, true)]
    [InlineData(1, 3, true, 2, 2, 2, 2, true)]
    [InlineData(1, 1, true, 2, 2, 2, 2, false)]
    [InlineData(1, 2, false, 2, 2, 2, 2, false)]
    [InlineData(1, 2, true, 2, 2, 2, 1, false)]
    [InlineData(1, 2, true, 2, 0, 2, 2, false)]
    [InlineData(1, 2, true, 1, 1, 0, 0, false)]
    [InlineData(0, 2, true, 2, 2, 2, 2, false)]
    public void TocStatusRequiresEveryExpectedHeadingAndPageNumber(
        int updatedIndexes,
        int updatePassCount,
        bool indexConverged,
        int entryLines,
        int pageNumbers,
        int expectedHeadings,
        int matchedHeadings,
        bool expected)
    {
        Assert.Equal(expected, DocumentRenderer.IsVerifiedTocStatus(
            updatedIndexes,
            updatePassCount,
            indexConverged,
            entryLines,
            pageNumbers,
            expectedHeadings,
            matchedHeadings));
    }

    [Theory]
    [InlineData(true, 10, 10, true)]
    [InlineData(true, 11, 10, false)]
    [InlineData(true, 0, 10, false)]
    [InlineData(false, 0, 10, true)]
    public void TocPageNumbersMustFitTheRenderedPdf(
        bool verifyToc,
        int maxPageNumber,
        int renderedPageCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            DocumentRenderer.AreTocPageNumbersWithinRange(
                verifyToc,
                maxPageNumber,
                renderedPageCount));
    }
}
