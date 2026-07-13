using LlamaMcp;

namespace LlamaMcp.Tests;

// Guards the one non-obvious bit of the empty-content fix (v1.3): what counts
// as "empty". A backend can return finishReason:"stop" with blank/whitespace
// content, and both the chat path (IsEmpty) and the job path (CompletedEmpty)
// route their decision through this helper -- so the null/whitespace handling
// is worth pinning. Live LM Studio remains the integration check for the rest.
public class ContentValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("  \t\r\n  ")]
    public void IsEmptyContent_true_for_null_or_blank(string? content)
        => Assert.True(ContentValidation.IsEmptyContent(content));

    [Theory]
    [InlineData("hello")]
    [InlineData(" hi ")]
    [InlineData("0")]
    [InlineData("The sea stretches out.")]
    public void IsEmptyContent_false_for_real_content(string content)
        => Assert.False(ContentValidation.IsEmptyContent(content));
}
