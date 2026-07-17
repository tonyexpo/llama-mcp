namespace LlamaMcp;

// Single source of truth for "which model does this job item's stats count
// against" -- get_model_stats used to group by JobItem.Job.Model alone, which
// is often "" (a caller omitted `model` to use whatever LM Studio has
// loaded), silently merging every such call into one bucket regardless of
// which real model actually answered it. Prefer the backend's own echoed
// model name (JobItem.ResolvedModel, populated on a successful call) over
// the request's Model field, and only fall back to an explicit label when
// neither is known (e.g. a Failed/Cancelled item that never got a response
// and never had a model requested either).
public static class JobModelAttribution
{
    public const string Unspecified = "(unspecified)";

    public static string EffectiveModel(string? resolvedModel, string requestedModel)
    {
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            return resolvedModel;
        }

        return string.IsNullOrEmpty(requestedModel) ? Unspecified : requestedModel;
    }
}
