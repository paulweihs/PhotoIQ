namespace PhotoIQPro.Core.Interfaces;

public enum OllamaSetupStage
{
    Checking,
    StartingOllama,
    Ready,
    Error,
}

public record OllamaSetupProgress(
    OllamaSetupStage Stage,
    string Message);

public interface IOllamaSetupService
{
    /// <summary>
    /// Verifies that Ollama is running locally and the required model is present.
    /// Never makes external network calls. If anything is missing, yields an Error
    /// stage — the installer is responsible for having placed everything on disk.
    /// </summary>
    IAsyncEnumerable<OllamaSetupProgress> EnsureReadyAsync(string modelName, CancellationToken ct = default);
}
