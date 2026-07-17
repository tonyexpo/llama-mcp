using Microsoft.EntityFrameworkCore;

namespace LlamaMcp;

public enum JobItemStatus
{
    Pending,
    Running,
    Completed,

    // Terminal, like Completed: the backend call succeeded but returned
    // empty content (finishReason:"stop" with nothing generated -- see
    // CLAUDE.md v1.3). Persisted via HasConversion<string>() below, so
    // adding this member is additive and doesn't disturb existing rows.
    CompletedEmpty,
    Failed,
    Cancelled,
}

public sealed class Job
{
    public Guid Id { get; set; }

    // DateTime (UTC), not DateTimeOffset: the SQLite EF Core provider can't
    // translate ORDER BY over DateTimeOffset columns server-side.
    public DateTime CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public double? TopP { get; set; }
    public bool? EnableThinking { get; set; }
}

public sealed class JobItem
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int Index { get; set; }
    public JobItemStatus Status { get; set; }
    public string? Label { get; set; }
    public string MessagesJson { get; set; } = "";
    public string? ResultContent { get; set; }
    public string? ResultFinishReason { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // From the backend's "usage" object, when present (v1.4). Added via a
    // startup ALTER TABLE patch, not EF migrations -- see Program.cs.
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    // The model name the backend actually echoed back in a successful
    // response, distinct from Job.Model (what the caller requested, often
    // "" when omitted to use whatever LM Studio has loaded). get_model_stats
    // groups by this when present -- grouping by Job.Model alone silently
    // merged every omitted-model call into one "" bucket regardless of which
    // real model answered it. Null for items that never got a response
    // (Failed/Cancelled before completion).
    public string? ResolvedModel { get; set; }
}

public sealed class JobDbContext(DbContextOptions<JobDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobItem> JobItems => Set<JobItem>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<JobItem>()
            .Property(i => i.Status)
            .HasConversion<string>();

        builder.Entity<JobItem>()
            .HasIndex(i => new { i.JobId, i.Index });
    }
}
