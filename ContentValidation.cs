namespace LlamaMcp;

// Single source of truth for "is this result content empty" -- a backend can
// return finishReason:"stop" (a normal, successful-looking completion) with
// no actual content (seen live on qwen3.6-27b-mtp, see CLAUDE.md v1.3). Both
// chat and job paths must route through this instead of re-checking
// IsNullOrWhiteSpace independently.
public static class ContentValidation
{
    public static bool IsEmptyContent(string? content) => string.IsNullOrWhiteSpace(content);
}
