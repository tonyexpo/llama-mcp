using LlamaMcp;

namespace LlamaMcp.Tests;

// Guards the get_model_stats grouping fix: prefer the backend's own echoed
// model name over the (often omitted) request field, and never silently
// group unknown-model items under "" -- see CLAUDE.md's "v1.3-v1.5 live QA".
public class JobModelAttributionTests
{
    [Fact]
    public void Prefers_resolved_model_when_present()
        => Assert.Equal("qwen/qwen3.5-9b", JobModelAttribution.EffectiveModel("qwen/qwen3.5-9b", ""));

    [Fact]
    public void Resolved_model_wins_even_if_a_different_model_was_requested()
        => Assert.Equal("actual-model", JobModelAttribution.EffectiveModel("actual-model", "requested-model"));

    [Fact]
    public void Falls_back_to_requested_model_when_resolved_is_missing()
        => Assert.Equal("requested-model", JobModelAttribution.EffectiveModel(null, "requested-model"));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Falls_back_to_unspecified_label_when_neither_is_known(string? resolved, string requested)
        => Assert.Equal(JobModelAttribution.Unspecified, JobModelAttribution.EffectiveModel(resolved, requested));
}
