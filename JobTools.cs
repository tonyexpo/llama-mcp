using System.ComponentModel;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace LlamaMcp;

[McpServerToolType]
public sealed class JobTools(IDbContextFactory<JobDbContext> dbFactory, ChannelWriter<Guid> channelWriter, JobCancellationRegistry cancellations)
{
    [McpServerTool(Name = "submit_job"), Description("Submit a batch of chat items to be processed in the background against the local backend. Returns immediately with a jobId -- use get_job_status/get_job_result to check on it later instead of waiting. Use this instead of chat for large batches (many documents/images) where a single synchronous call would run too long or where the caller shouldn't stay blocked waiting.")]
    public async Task<SubmitJobResult> SubmitJob(
        [Description("Items to process, each shaped like the chat tool's messages list. Every item in a job shares the same model/temperature/maxTokens/topP/enableThinking.")] List<JobItemInputDto> items,
        [Description("Model name shared by all items. Omit to use the server-configured default model.")] string? model = null,
        [Description("Sampling temperature shared by all items. Omit to use the backend default.")] double? temperature = null,
        [Description("Maximum tokens to generate per item. Omit to use the backend default.")] int? maxTokens = null,
        [Description("Nucleus sampling top-p shared by all items. Omit to use the backend default.")] double? topP = null,
        [Description("Disable reasoning/\"thinking\" for every item in this job. See the chat tool for details.")] bool? enableThinking = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("items must contain at least one entry.", nameof(items));
        }

        var job = new Job
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            Model = model ?? "",
            Temperature = temperature,
            MaxTokens = maxTokens,
            TopP = topP,
            EnableThinking = enableThinking,
        };

        var jobItems = items.Select((input, index) => new JobItem
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Index = index,
            Status = JobItemStatus.Pending,
            Label = input.Label,
            MessagesJson = JsonSerializer.Serialize(input.Messages),
            CreatedAt = DateTime.UtcNow,
        }).ToList();

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Jobs.Add(job);
            db.JobItems.AddRange(jobItems);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var item in jobItems)
        {
            await channelWriter.WriteAsync(item.Id, cancellationToken);
        }

        return new SubmitJobResult { JobId = job.Id, ItemCount = jobItems.Count };
    }

    [McpServerTool(Name = "get_job_status"), Description("Check progress of a batch job submitted via submit_job, without pulling in result content. Cheap to call repeatedly.")]
    public async Task<JobStatusResult> GetJobStatus(
        [Description("Job ID returned by submit_job.")] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var counts = await db.JobItems
            .Where(i => i.JobId == jobId)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        if (counts.Count == 0 && !await db.Jobs.AnyAsync(j => j.Id == jobId, cancellationToken))
        {
            throw new InvalidOperationException($"Job {jobId} not found.");
        }

        int CountOf(JobItemStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        var pending = CountOf(JobItemStatus.Pending);
        var running = CountOf(JobItemStatus.Running);
        var completed = CountOf(JobItemStatus.Completed);
        var completedEmpty = CountOf(JobItemStatus.CompletedEmpty);
        var failed = CountOf(JobItemStatus.Failed);
        var cancelled = CountOf(JobItemStatus.Cancelled);

        // Concurrency=1 means at most one Running item -- its StartedAt is
        // enough to give the caller an elapsed-time signal to set their own
        // cancel/alarm policy on (see CLAUDE.md v1.3). Server never auto-kills.
        var runningStartedAt = await db.JobItems
            .Where(i => i.JobId == jobId && i.Status == JobItemStatus.Running)
            .Select(i => i.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new JobStatusResult
        {
            JobId = jobId,
            Total = pending + running + completed + completedEmpty + failed + cancelled,
            Pending = pending,
            Running = running,
            Completed = completed,
            CompletedEmpty = completedEmpty,
            Failed = failed,
            Cancelled = cancelled,
            IsComplete = pending == 0 && running == 0,
            RunningForSeconds = runningStartedAt is null ? null : (long)(DateTime.UtcNow - runningStartedAt.Value).TotalSeconds,
        };
    }

    [McpServerTool(Name = "get_job_result"), Description("Fetch results for a batch job. Supports paging (offset/limit), fetching specific items by index, or filtering by status -- use this to avoid pulling all results into context at once.")]
    public async Task<JobResultPage> GetJobResult(
        [Description("Job ID returned by submit_job.")] Guid jobId,
        [Description("Number of items to skip, ordered by index. Ignored when indices is provided.")] int offset = 0,
        [Description("Maximum number of items to return. Ignored when indices is provided.")] int limit = 5,
        [Description("Fetch only these specific item indices, ignoring offset/limit.")] List<int>? indices = null,
        [Description("Only return items with this status, e.g. Failed to pull just the failures.")] JobItemStatus? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<JobItem> query = db.JobItems.Where(i => i.JobId == jobId);

        if (statusFilter is not null)
        {
            query = query.Where(i => i.Status == statusFilter);
        }

        var total = await query.CountAsync(cancellationToken);

        query = query.OrderBy(i => i.Index);
        query = indices is { Count: > 0 }
            ? query.Where(i => indices.Contains(i.Index))
            : query.Skip(offset).Take(limit);

        var items = await query
            .Select(i => new JobItemResultDto
            {
                Index = i.Index,
                Label = i.Label,
                Status = i.Status,
                Content = i.ResultContent,
                FinishReason = i.ResultFinishReason,
                Error = i.Error,
                StartedAt = i.StartedAt,
                CompletedAt = i.CompletedAt,
                PromptTokens = i.PromptTokens,
                CompletionTokens = i.CompletionTokens,
            })
            .ToListAsync(cancellationToken);

        // Computed post-fetch rather than in the query -- keeps the EF
        // projection to plain column reads, no TimeSpan math for the SQLite
        // provider to translate.
        foreach (var item in items)
        {
            if (item.StartedAt is not null && item.CompletedAt is not null)
            {
                item.DurationSeconds = (item.CompletedAt.Value - item.StartedAt.Value).TotalSeconds;
            }
        }

        return new JobResultPage { JobId = jobId, Total = total, Offset = offset, Limit = limit, Items = items };
    }

    [McpServerTool(Name = "cancel_job"), Description("Cancel a batch job. Items not yet started are cancelled immediately; an item already in progress has its backend call aborted too.")]
    public async Task<CancelJobResult> CancelJob(
        [Description("Job ID returned by submit_job.")] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var cancelled = await db.JobItems
            .Where(i => i.JobId == jobId && i.Status == JobItemStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, JobItemStatus.Cancelled)
                .SetProperty(i => i.CompletedAt, DateTime.UtcNow), cancellationToken);

        // Concurrency=1 -- at most one Running item for this job. Signal its
        // per-item CancellationTokenSource via the shared registry; if it's
        // there, JobProcessor's OperationCanceledException handler flips it
        // to Cancelled once the backend call actually aborts.
        var runningItemIds = await db.JobItems
            .Where(i => i.JobId == jobId && i.Status == JobItemStatus.Running)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var runningItemCancellationRequested = false;
        foreach (var id in runningItemIds)
        {
            if (cancellations.TryCancel(id))
            {
                runningItemCancellationRequested = true;
            }
        }

        return new CancelJobResult
        {
            JobId = jobId,
            CancelledCount = cancelled,
            RunningItemCancellationRequested = runningItemCancellationRequested,
        };
    }

    [McpServerTool(Name = "get_model_stats"), Description("Empirical per-model profile aggregated from this server's job history -- item counts by outcome, average duration, average completion tokens. Use this to pick the best-fit local model for a task from real observed behavior, not declared specs (a model's advertised size/capabilities don't reliably predict how it actually performs here).")]
    public async Task<List<ModelStatsDto>> GetModelStats(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // ponytail: SQLite/EF can't AVG a TimeSpan and there's no clean way to
        // get a median server-side, so pull just the columns needed for
        // terminal items and aggregate in memory with LINQ. Fine at
        // single-user jobs.db scale -- revisit only if history grows huge.
        var terminalStatuses = new[]
        {
            JobItemStatus.Completed,
            JobItemStatus.CompletedEmpty,
            JobItemStatus.Failed,
            JobItemStatus.Cancelled,
        };

        var rows = await db.JobItems
            .Where(i => terminalStatuses.Contains(i.Status))
            .Join(db.Jobs, i => i.JobId, j => j.Id, (i, j) => new
            {
                j.Model,
                i.Status,
                i.StartedAt,
                i.CompletedAt,
                i.CompletionTokens,
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Model)
            .Select(g =>
            {
                var durations = g
                    .Where(r => r.StartedAt is not null && r.CompletedAt is not null)
                    .Select(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds)
                    .ToList();

                var completionTokens = g
                    .Where(r => r.CompletionTokens is not null)
                    .Select(r => r.CompletionTokens!.Value)
                    .ToList();

                return new ModelStatsDto
                {
                    Model = g.Key,
                    TotalItems = g.Count(),
                    Completed = g.Count(r => r.Status == JobItemStatus.Completed),
                    CompletedEmpty = g.Count(r => r.Status == JobItemStatus.CompletedEmpty),
                    Failed = g.Count(r => r.Status == JobItemStatus.Failed),
                    Cancelled = g.Count(r => r.Status == JobItemStatus.Cancelled),
                    AvgDurationSeconds = durations.Count == 0 ? null : durations.Average(),
                    AvgCompletionTokens = completionTokens.Count == 0 ? null : completionTokens.Average(),
                };
            })
            .OrderByDescending(s => s.TotalItems)
            .ToList();
    }
}

public sealed class JobItemInputDto
{
    public List<ChatMessageDto> Messages { get; set; } = [];

    [Description("Optional caller-supplied label (e.g. filename/title) to correlate this item's result back to its source, instead of relying only on array index.")]
    public string? Label { get; set; }
}

public sealed class SubmitJobResult
{
    public Guid JobId { get; set; }
    public int ItemCount { get; set; }
}

public sealed class JobStatusResult
{
    public Guid JobId { get; set; }
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int CompletedEmpty { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public bool IsComplete { get; set; }

    // Elapsed seconds since the currently-running item started, null when
    // nothing is running. The server never auto-cancels a slow item -- this
    // is only a signal for the caller to set their own alarm/cancel policy.
    public long? RunningForSeconds { get; set; }
}

public sealed class JobResultPage
{
    public Guid JobId { get; set; }
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public List<JobItemResultDto> Items { get; set; } = [];
}

public sealed class JobItemResultDto
{
    public int Index { get; set; }
    public string? Label { get; set; }
    public JobItemStatus Status { get; set; }
    public string? Content { get; set; }
    public string? FinishReason { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Null while pending/running (no CompletedAt yet).
    public double? DurationSeconds { get; set; }

    // From the backend's "usage" object, when present (v1.4).
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
}

public sealed class CancelJobResult
{
    public Guid JobId { get; set; }
    public int CancelledCount { get; set; }

    // True when a Running item existed and its backend call was signalled to
    // abort. The item lands in Cancelled once JobProcessor's cancellation
    // handler actually observes it -- not necessarily by the time this
    // result is returned.
    public bool RunningItemCancellationRequested { get; set; }
}

public sealed class ModelStatsDto
{
    public string Model { get; set; } = "";
    public int TotalItems { get; set; }
    public int Completed { get; set; }
    public int CompletedEmpty { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public double? AvgDurationSeconds { get; set; }
    public double? AvgCompletionTokens { get; set; }
}
