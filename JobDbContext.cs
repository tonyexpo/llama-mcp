using Microsoft.EntityFrameworkCore;

namespace LlamaMcp;

public enum JobItemStatus
{
    Pending,
    Running,
    Completed,
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
