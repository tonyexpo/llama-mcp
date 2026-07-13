---
name: llama-mcp-consumer
description: Use whenever a session is about to delegate/offload work to a local LLM through the llama-mcp connector (chat, submit_job, get_job_status, get_job_result, cancel_job, get_model_stats), or when the goal is saving Claude/Anthropic tokens by batching documents/images/translation/classification work to a local model on the user's own hardware instead of doing it on Claude directly. Read this before calling llama-mcp tools, deciding whether a task belongs on the local model vs Claude, or sizing maxTokens/verification effort for a local batch.
---

# Using llama-mcp effectively

For whoever *calls* llama-mcp's tools (Claude, or another orchestrator) — not for developing the connector itself. Distilled from a live multi-model test session (see `invocation-test-results.md`) plus the v1.4 round; each point below is a lesson paid for by a real failure mode observed in testing.

## 0. Keep-local vs escalate-to-Claude rubric

Before delegating anything, check it actually fits the reason this connector exists: saving paid Claude tokens on work that doesn't need Claude's reasoning.

- **Keep local** for deterministic, high-volume transforms whose output is cheap to verify: translation, extraction, classification, vision labeling, simple drafts. These are exactly the tasks a local model can do at acceptable quality and where a wrong answer is easy to spot and cheap to fix.
- **Escalate to Claude** when the task needs real reasoning, is high-stakes single-shot (one answer, no batch to average over, wrong output is costly), or when *verifying* the local output would itself cost more Claude tokens than just doing the task on Claude in the first place. Verification cost is not free — it counts against the token budget you're trying to protect.
- This rule exists to stop scope creep in the other direction too: it's tempting to route everything local "to save tokens," but a local model producing subtly wrong output that then needs full manual Claude review has saved nothing. The rubric protects the actual north star (save paid tokens net of verification cost), not "avoid Claude at all costs."

## 1. Empty content is failure, regardless of `finishReason`

The single most important rule: **check for empty content explicitly, don't trust `finishReason` alone.** A backend can return a normal-looking `finishReason: "stop"` (not `"length"`) with zero actual content — observed on a real model in testing, 6 of 20 items in one batch. `"stop"` alone does not mean success.

- `chat` sets `IsEmpty: true` on its result when this happens — check it instead of hand-rolling an `IsNullOrWhiteSpace` check on `Content`.
- Job items get a distinct terminal status, `CompletedEmpty`, instead of `Completed` — filter `get_job_result` with `statusFilter: CompletedEmpty` to find them, same as you would `Failed`.
- Treat both the same as an error: retry, flag, or drop the item — don't count it as a successful result.

## 2. Sample-verify, don't full-verify

Even a clean `finishReason: "stop"` with real (non-empty) content can be confidently wrong. In one test, a vision model asked to name the dominant color of a solid white 64x64 image answered "black" — not a near-miss on a similar hue (the kind of error the other 19/20 items made, e.g. orange→brown), but a full categorical inversion, delivered with the same "success" signal as a correct answer. Mechanical gates (`IsEmpty`/`CompletedEmpty`) cannot catch this class of error; only checking the actual content can.

Full verification by Claude on every item erases the token savings that are the entire point of delegating to a local model. Instead:

1. Rely on the server-side mechanical gates first (`IsEmpty`, `CompletedEmpty`) — they catch the cheap, common failure mode for free.
2. Spot-check a small sample of the batch (2-3 items out of N, picked across different labels/positions, not just the first few) by actually reading the content.
3. If the sample passes, accept the batch. If the sample fails, escalate the *whole* batch for closer review or redo it — don't try to guess which other items are also wrong.

This bounds verification cost to a small, fixed sample regardless of batch size, while still catching systematic problems (a bad prompt, a model that's unreliable on this task) that would show up in any sample you happened to check.

## 3. Chunk long documents instead of sending them whole

For a long document, prefer splitting it into N chunk-items submitted as one `submit_job`, rather than one item containing the whole document:

- **Failure isolation**: one bad chunk (empty/wrong) doesn't force redoing the entire document — retry or flag just that chunk.
- **Progress granularity**: `get_job_status` counts become meaningful progress on a long-running document instead of one opaque all-or-nothing item.
- **Less context-overflow pressure**: a smaller per-chunk prompt is less likely to hit a backend's context-overflow truncation policy (see §4 below), which can silently drop parts of the prompt (including system instructions) once total context gets tight.

Current limitation: `maxTokens`/`temperature`/`topP`/`enableThinking` are shared across every item in a job — you can't give one chunk a different budget than another within the same `submit_job` call. Size the shared `maxTokens` for the largest/hardest chunk you expect.

## 4. Size `maxTokens` generously, but not unbounded

Low `maxTokens` is the most common cause of empty responses: template overhead, image encoding, and (on reasoning models) a hidden "thinking" phase all eat into the budget *before* the visible answer, so a tight budget produces `finishReason: "length"` with nothing written.

- Budget generously: roughly **80% of the model's real usable context**, not its advertised max and not a "safe-looking" small number.
- Never leave it unbounded either — a degenerate item (repetition loop, missing stop token, common on small/quantized local models) can burn the entire budget before stopping. On a mono-thread backend (see §6) that blocks the whole queue for the duration.
- "Real usable context" can be smaller than advertised if the backend's context-overflow policy (sliding window / truncate-middle) silently drops part of the prompt under pressure. If you control the backend config, prefer it fail loudly on overflow rather than truncate silently.
- **v1.4**: `chat`/job-item results now include `PromptTokens`/`CompletionTokens` from the backend's real usage data (when the backend sends it). Use the observed `CompletionTokens` from a prior run of the same task/model to size `maxTokens` on the next one, instead of guessing blind every time.

## 5. Verify vision capability with one image first

Not every model that accepts the `imageUrls` field can actually handle it — one tested model returned a hard `400 Bad Request` on every single image in a batch, despite the tool schema allowing it. Before submitting a large image batch: send **one** small image through `chat` first and confirm you get a real answer back. Only then submit the batch.

- **v1.4**: backend error responses now surface the actual error body (truncated to ~500 chars) instead of just a bare status code — a rejected-image 400 now tells you why, not just that it failed.

## 6. Concurrency is 1 — async removes blocking, not wait time

The local backend processes one item at a time, even with multiple jobs queued. `submit_job` exists so the caller (you) isn't blocked waiting and doesn't risk a client-side timeout on a long call — it does not make the underlying work faster. A second job submitted while one is `running` sits entirely `pending` until the first finishes. Plan batch sizes and expectations accordingly.

## 7. Use submit → poll → fetch, and label your items

For anything that might run long (a full document, an image batch), don't use `chat`:

1. `submit_job` with your items (chunked per §3 for long documents) — give each one a `label` (filename, chunk index, whatever identifies it) so you can correlate results later. Don't rely on array order.
2. Poll `get_job_status` periodically (cheap, no content pulled).
3. Once items complete, fetch results with `get_job_result`, filtering by `statusFilter` or specific `indices` as needed instead of pulling everything into context at once.

## 8. Watch `RunningForSeconds` and cancel a stuck item if needed

`get_job_status` reports `RunningForSeconds`: elapsed time since the currently-running item started (null when nothing is running — concurrency=1 means at most one). The server does **not** auto-cancel a slow item — there's no reliable way to tell "still working" from "stuck" by token count or elapsed time alone, and the wrong guess loses real (if slow) output.

- **v1.4**: `cancel_job` now actually aborts the in-flight backend call for a `Running` item (not just the not-yet-started `Pending` ones) — previously the only way to get unstuck from one glacial item monopolizing the queue was restarting the server. If an item runs far longer than you're willing to wait, call `cancel_job` and it will land in `Cancelled` once the abort is observed.

## 9. Pick a model from observed behavior, not declared specs

Advertised size/context/capabilities don't reliably predict how a model actually performs on this backend — in testing, the smallest model tried beat two much larger ones on translation speed/quality and vision accuracy, and a larger reasoning model didn't support vision at all.

- **v1.4**: `get_model_stats` returns an empirical per-model profile built from this server's own job history — item counts by outcome (completed/completedEmpty/failed/cancelled), average duration, and average completion tokens. Use it to pick the best-fit local model for a task from real observed behavior on *this* hardware/backend, instead of the model's marketing numbers. It only reflects jobs that have actually run — a model with no history yet won't show up.

## 10. Don't rely on `enableThinking` doing anything

The `enableThinking` flag is real and forwarded to the backend, but on LM Studio it showed no reliable, measurable effect in testing — same minimum token thresholds and same behavior whether `true`, `false`, or omitted (likely an upstream LM Studio/Qwen bug, not a connector issue). Treat it as a harmless forward-compatible flag, not a lever you can count on for cost control today.
