using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace LlamaMcp;

// Sequential (concurrency=1) background worker for job items -- matches
// realistic local hardware (one GPU/model instance). Item dispatch is a
// hybrid: an in-memory channel gives low-latency pickup of freshly submitted
// items, and a startup sweep requeues anything left Pending/Running from a
// prior process (crash/restart) so durability doesn't depend purely on the
// channel. Every claim goes through a conditional UPDATE (Pending->Running,
// 0 rows affected = already claimed/cancelled) so double-enqueue from the
// submit/sweep race, a cancel racing an in-flight dequeue, and any future
// re-sweep are all safe by construction rather than needing separate fixes.
public sealed class JobProcessor(
    IDbContextFactory<JobDbContext> dbFactory,
    LlamaBackendClient backend,
    ChannelReader<Guid> channelReader,
    ChannelWriter<Guid> channelWriter,
    ILogger<JobProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueOrphanedItemsAsync(stoppingToken);

        await foreach (var itemId in channelReader.ReadAllAsync(stoppingToken))
        {
            await ProcessItemAsync(itemId, stoppingToken);
        }
    }

    private async Task RequeueOrphanedItemsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Anything still "Running" belonged to a process that died mid-item.
        // Reset before enqueueing so get_job_status never reports a phantom
        // Running item with nothing actually working on it.
        await db.JobItems
            .Where(i => i.Status == JobItemStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, JobItemStatus.Pending), ct);

        var pendingIds = await db.JobItems
            .Where(i => i.Status == JobItemStatus.Pending)
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Index)
            .Select(i => i.Id)
            .ToListAsync(ct);

        foreach (var id in pendingIds)
        {
            await channelWriter.WriteAsync(id, ct);
        }
    }

    private async Task ProcessItemAsync(Guid itemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var claimed = await db.JobItems
            .Where(i => i.Id == itemId && i.Status == JobItemStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, JobItemStatus.Running)
                .SetProperty(i => i.StartedAt, DateTime.UtcNow), ct);

        if (claimed == 0)
        {
            // Already claimed, cancelled, or completed -- not an error.
            return;
        }

        var item = await db.JobItems.FirstAsync(i => i.Id == itemId, ct);
        var job = await db.Jobs.FirstAsync(j => j.Id == item.JobId, ct);

        try
        {
            var request = new ChatCompletionRequestDto
            {
                Model = job.Model,
                Messages = JsonSerializer.Deserialize<List<ChatMessageDto>>(item.MessagesJson) ?? [],
                Temperature = job.Temperature,
                MaxTokens = job.MaxTokens,
                TopP = job.TopP,
                ChatTemplateKwargs = job.EnableThinking is null
                    ? null
                    : new ChatTemplateKwargsDto { EnableThinking = job.EnableThinking },
            };

            var response = await backend.ChatAsync(request, ct);
            var choice = response.Choices.FirstOrDefault();

            item.Status = JobItemStatus.Completed;
            item.ResultContent = choice?.Message.Content ?? "";
            item.ResultFinishReason = choice?.FinishReason;
            item.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One item failing must not kill the loop over the rest of the batch.
            item.Status = JobItemStatus.Failed;
            item.Error = ex.Message;
            item.CompletedAt = DateTime.UtcNow;
            logger.LogWarning(ex, "Job item {ItemId} (job {JobId}) failed", itemId, item.JobId);
        }

        await db.SaveChangesAsync(ct);
    }
}
