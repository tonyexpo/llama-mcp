# Consumer guide: using llama-mcp effectively

For whoever *calls* this MCP's tools (Claude, or another orchestrator) — not for developing the connector itself. Distilled from a live multi-model test session (see `invocation-test-results.md`); each point below is a lesson paid for by a real failure mode observed in testing.

## 1. Empty content is failure, regardless of `finishReason`

The single most important rule: **check for empty content explicitly, don't trust `finishReason` alone.** A backend can return a normal-looking `finishReason: "stop"` (not `"length"`) with zero actual content — observed on a real model in testing, 6 of 20 items in one batch. `"stop"` alone does not mean success.

- `chat` sets `IsEmpty: true` on its result when this happens — check it instead of hand-rolling an `IsNullOrWhiteSpace` check on `Content`.
- Job items get a distinct terminal status, `CompletedEmpty`, instead of `Completed` — filter `get_job_result` with `statusFilter: CompletedEmpty` to find them, same as you would `Failed`.
- Treat both the same as an error: retry, flag, or drop the item — don't count it as a successful result.

## 2. Size `maxTokens` generously, but not unbounded

Low `maxTokens` is the most common cause of empty responses: template overhead, image encoding, and (on reasoning models) a hidden "thinking" phase all eat into the budget *before* the visible answer, so a tight budget produces `finishReason: "length"` with nothing written.

- Budget generously: roughly **80% of the model's real usable context**, not its advertised max and not a "safe-looking" small number.
- Never leave it unbounded either — a degenerate item (repetition loop, missing stop token, common on small/quantized local models) can burn the entire budget before stopping. On a mono-thread backend (see §4) that blocks the whole queue for the duration.
- "Real usable context" can be smaller than advertised if the backend's context-overflow policy (sliding window / truncate-middle) silently drops part of the prompt under pressure. If you control the backend config, prefer it fail loudly on overflow rather than truncate silently.

## 3. Verify vision capability with one image first

Not every model that accepts the `imageUrls` field can actually handle it — one tested model returned a hard `400 Bad Request` on every single image in a batch, despite the tool schema allowing it. Before submitting a large image batch: send **one** small image through `chat` first and confirm you get a real answer back. Only then submit the batch.

## 4. Concurrency is 1 — async removes blocking, not wait time

The local backend processes one item at a time, even with multiple jobs queued. `submit_job` exists so the caller (you) isn't blocked waiting and doesn't risk a client-side timeout on a long call — it does not make the underlying work faster. A second job submitted while one is `running` sits entirely `pending` until the first finishes. Plan batch sizes and expectations accordingly.

## 5. Use submit → poll → fetch, and label your items

For anything that might run long (a full document, an image batch), don't use `chat`:

1. `submit_job` with your items — give each one a `label` (filename, title, whatever identifies it) so you can correlate results later. Don't rely on array order.
2. Poll `get_job_status` periodically (cheap, no content pulled).
3. Once items complete, fetch results with `get_job_result`, filtering by `statusFilter` or specific `indices` as needed instead of pulling everything into context at once.

## 6. Watch `RunningForSeconds` and decide your own cutoff

`get_job_status` reports `RunningForSeconds`: elapsed time since the currently-running item started (null when nothing is running — concurrency=1 means at most one). The server does **not** auto-cancel a slow item — there's no reliable way to tell "still working" from "stuck" by token count or elapsed time alone, and the wrong guess loses real (if slow) output. If an item runs far longer than you're willing to wait, decide for yourself whether to keep polling or call `cancel_job`.

## 7. Don't rely on `enableThinking` doing anything

The `enableThinking` flag is real and forwarded to the backend, but on LM Studio it showed no reliable, measurable effect in testing — same minimum token thresholds and same behavior whether `true`, `false`, or omitted (likely an upstream LM Studio/Qwen bug, not a connector issue). Treat it as a harmless forward-compatible flag, not a lever you can count on for cost control today.
