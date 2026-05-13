using CommunityToolkit.Mvvm.ComponentModel;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Desktop.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly IOllamaSetupService _setup;
    private readonly string _modelName;

    [ObservableProperty] private string _statusMessage = "Starting up…";
    [ObservableProperty] private bool   _isError       = false;
    [ObservableProperty] private bool   _isChecking    = true;

    /// <summary>Fired when setup succeeds. The window subscribes and closes itself.</summary>
    public event Action? SetupComplete;

    public SetupViewModel(IOllamaSetupService setup, string modelName)
    {
        _setup     = setup;
        _modelName = modelName;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var progress in _setup.EnsureReadyAsync(_modelName, ct))
            {
                StatusMessage = progress.Message;
                IsError       = progress.Stage == OllamaSetupStage.Error;
                IsChecking    = progress.Stage is OllamaSetupStage.Checking or OllamaSetupStage.StartingOllama;

                if (progress.Stage == OllamaSetupStage.Ready)
                {
                    IsChecking = false;
                    // Brief pause so the user sees "AI engine ready".
                    await Task.Delay(500, ct);
                    SetupComplete?.Invoke();
                    return;
                }

                if (progress.Stage == OllamaSetupStage.Error)
                {
                    IsChecking = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* user dismissed via Continue Anyway */ }
        catch (Exception ex)
        {
            StatusMessage = $"Unexpected error: {ex.Message}";
            IsError       = true;
            IsChecking    = false;
        }
    }
}
