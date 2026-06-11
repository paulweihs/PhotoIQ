namespace PhotoWell.Core.Interfaces;

public enum OllamaSetupStage
{
    /// <summary>Checking whether Ollama is installed and the model is present.</summary>
    Checking,
    /// <summary>Ollama is installed but not running; attempting to start it.</summary>
    StartingOllama,
    /// <summary>Ollama is not installed; downloading the installer from ollama.com.</summary>
    DownloadingOllama,
    /// <summary>Running the Ollama installer silently.</summary>
    InstallingOllama,
    /// <summary>Ollama is ready but the vision model is missing; pulling it via ollama pull.</summary>
    PullingModel,
    /// <summary>Everything is ready — Ollama running, model present.</summary>
    Ready,
    /// <summary>A recoverable error occurred; waiting for user to retry.</summary>
    Error,
}

public record OllamaSetupProgress(
    OllamaSetupStage Stage,
    string Message,
    /// <summary>Bytes downloaded so far (for DownloadingOllama / PullingModel stages). -1 if unknown.</summary>
    long BytesDownloaded = -1,
    /// <summary>Total bytes expected (for DownloadingOllama / PullingModel stages). -1 if unknown.</summary>
    long BytesTotal = -1);

public interface IOllamaSetupService
{
    /// <summary>
    /// Ensures Ollama is installed, running, and the required model is present.
    /// Downloads Ollama from ollama.com if not installed (checks first — works offline
    /// if Ollama was pre-installed from an external drive).
    /// Pulls the vision model via <c>ollama pull</c> if missing (resumes partial pulls).
    /// Yields progress events throughout; never throws — errors are reported as Error stage.
    /// </summary>
    IAsyncEnumerable<OllamaSetupProgress> EnsureReadyAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    /// Silently ensures a chat/tool-calling model is present in Ollama.
    /// Assumes Ollama is already running (call after EnsureReadyAsync succeeds).
    /// Fire-and-forget safe — errors are logged but never thrown.
    /// </summary>
    Task EnsureChatModelAsync(string modelName, CancellationToken ct = default);
}
