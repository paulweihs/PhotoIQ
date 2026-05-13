# Resume: Ollama num_ctx Performance Investigation

**Problem:** Analysis is 47–125 s/photo instead of 5–7 s/photo.
**Root cause:** Ollama loads the model at num_ctx=2048 (default) and ignores per-request num_ctx=1024, causing KV cache to overflow VRAM into RAM.

## What was just implemented (not yet tested)

In `OllamaSetupService.RunSetupAsync`, after the model is confirmed present:
1. Calls `OllamaClient.GetRunningContextSizeAsync` → queries `/api/ps`
2. If running ctx != 1024, calls `UnloadModelAsync` (keep_alive=0) to evict model
3. First inference then reloads at num_ctx=1024 → should restore 5–7 s/photo

Key files changed:
- `src/PhotoWell.Services/Vision/OllamaClient.cs` — added `TargetNumCtx = 1024` const
- `src/PhotoWell.Services/Vision/OllamaSetupService.cs` — injected OllamaClient, added ctx check
- `src/PhotoWell.Desktop/App.xaml.cs` — updated DI to pass OllamaClient to OllamaSetupService

## Investigation steps

1. Launch PhotoWell, check logs for: `[OllamaSetup] Running num_ctx=` line
2. Run `ollama ps` after launch — confirm CONTEXT shows 1024
3. Time a batch of photos — should be 5–7 s/photo

## If still broken — alternatives to try

**A) Unconditional unload on startup** (simplest, cold-start ~30s once per session):
- Remove the `runningCtx != TargetNumCtx` condition, always call UnloadModelAsync

**B) Check model name matching** — `/api/ps` may return `minicpm-v:latest` but we match against `minicpm-v`. The current code uses `StartsWith(model + ":")` which should handle this — verify with actual `/api/ps` output.

**C) Timing issue** — unload fires but inference starts before Ollama finishes evicting. Add a short delay or poll `/api/ps` until the model disappears before signaling Ready.

**D) Use ollama CLI stop** instead of keep_alive=0 API:
```csharp
Process.Start("ollama", $"stop {modelName}");
```

## Known good state (5.7 s/photo)

This worked after manually running `ollama stop minicpm-v` in terminal, then launching PhotoWell. That confirms the fix is correct — the only question is whether the automated unload in OllamaSetupService achieves the same result reliably.
