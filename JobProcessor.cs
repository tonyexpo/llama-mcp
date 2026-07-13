using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace LlamaMcp;

// Singleton registry of per-item cancellation sources, keyed by JobItem.Id --
// lets cancel_job (JobTools) abort a backend call JobProcessor is actively
// awaiting, not just items that haven't started yet. Registered/removed
// around the backend call in JobProcessor.ProcessItemAsync.
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public void Register(Guid itemId, CancellationTokenSource cts) => _tokens[itemId] = cts;

    public void Unregister(Guid itemId) => _tokens.TryRemove(itemId, out _);

    public bool TryCancel(Guid itemId)
    {
        if (!_tokens.TryGetValue(itemId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The item finished and its finally block disposed the CTS in the
            // window between the TryGetValue above and here -- nothing left to
            // cancel, treat as "no running item to signal" rather than throw.
            return false;
        }

        return true;
    }
}

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
    JobCancellationRegistry cancellations,
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

        // Per-item cancellation source, separate from stoppingToken -- lets
        // cancel_job (JobTools, via the shared registry) abort just this
        // item's backend call without tearing down the whole processor loop.
        var itemCts = new CancellationTokenSource();
        cancellations.Register(itemId, itemCts);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, itemCts.Token);

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

            var response = await backend.ChatAsync(request, linkedCts.Token);
            var choice = response.Choices.FirstOrDefault();
            var content = choice?.Message.Content ?? "";

            // A successful call with empty content (finishReason:"stop", not
            // "length") is not the same as a real error -- see CLAUDE.md v1.3.
            item.Status = ContentValidation.IsEmptyContent(content) ? JobItemStatus.CompletedEmpty : JobItemStatus.Completed;
            item.ResultContent = content;
            item.ResultFinishReason = choice?.FinishReason;
            item.PromptTokens = response.Usage?.PromptTokens;
            item.CompletionTokens = response.Usage?.CompletionTokens;
            item.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                // Server shutdown, not a per-item cancel_job -- rethrow and
                // let ExecuteAsync's loop unwind. Leave the item Running: the
                // startup requeue sweep resets/requeues it on next launch.
                throw;
            }

            // Only the per-item token was cancelled -- a real cancel_job.
            item.Status = JobItemStatus.Cancelled;
            item.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // One item failing must not kill the loop over the rest of the batch.
            item.Status = JobItemStatus.Failed;
            item.Error = ex.Message;
            item.CompletedAt = DateTime.UtcNow;
            logger.LogWarning(ex, "Job item {ItemId} (job {JobId}) failed", itemId, item.JobId);
        }
        finally
        {
            cancellations.Unregister(itemId);
            itemCts.Dispose();
        }

        await db.SaveChangesAsync(ct);
    }
}
