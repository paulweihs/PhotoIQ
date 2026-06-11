using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Services.Vision;

namespace PhotoWell.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IExclusionRepository _exclusionRepo;
    private readonly OllamaClient _ollamaClient;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Set to true when adding a full-path exclusion removes photos from the library.
    /// Checked by MainViewModel after the Settings dialog closes to decide whether to reload.
    /// </summary>
    public bool LibraryModified { get; private set; }

    // ── AI Engine ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresRestart))]
    private string _ollamaUrl;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresRestart))]
    private string _visionModel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _chatModel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresRestart))]
    private string _clipModelsPath;
    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private bool   _connectionOk;
    [ObservableProperty] private bool   _isTestingConnection;

    /// <summary>Model names fetched from Ollama — populates the Vision Model dropdown.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    // ── Performance ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _isExpressMode;
    [ObservableProperty] private int  _maxThreads;
    [ObservableProperty] private bool _highQualityThumbnails;
    [ObservableProperty] private bool _useImperialUnits;
    [ObservableProperty] private bool _enableFaceDetection;

    [ObservableProperty] private int _visionImageSize;

    partial void OnVisionImageSizeChanged(int value)
    {
        OnPropertyChanged(nameof(IsVisionFast));
        OnPropertyChanged(nameof(IsVisionBalanced));
        OnPropertyChanged(nameof(IsVisionQuality));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public bool IsVisionFast     { get => VisionImageSize == 512;  set { if (value) VisionImageSize = 512;  } }
    public bool IsVisionBalanced { get => VisionImageSize == 768;  set { if (value) VisionImageSize = 768;  } }
    public bool IsVisionQuality  { get => VisionImageSize == 1024; set { if (value) VisionImageSize = 1024; } }

    // ── Startup ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showTipsOnStartup;

    partial void OnShowTipsOnStartupChanged(bool value)
    {
        UserPreferences.Current.TipsEnabled = value;
        UserPreferences.Current.Save();
    }

    // ── Exclusions ───────────────────────────────────────────────────────
    public ObservableCollection<ExclusionEntry> Exclusions { get; } = [];
    [ObservableProperty] private string _newExclusion = "";
    [ObservableProperty] private string _exclusionStatusText = "";

    // ── External Editors ─────────────────────────────────────────────────
    public ObservableCollection<ExternalEditorEntry> ExternalEditors { get; } = [];
    [ObservableProperty] private string _newEditorName = "";
    [ObservableProperty] private string _newEditorPath = "";

    // Original AI-engine values — used to detect restart-required changes.
    private readonly string _origOllamaUrl;
    private readonly string _origVisionModel;
    private readonly string _origClipModelsPath;

    /// <summary>True when any setting that requires a restart has been changed since last save.</summary>
    public bool RequiresRestart =>
        OllamaUrl.Trim()      != _origOllamaUrl      ||
        VisionModel.Trim()    != _origVisionModel     ||
        ClipModelsPath.Trim() != _origClipModelsPath;

    /// <summary>True when any setting differs from the last-persisted state (UserPreferences on disk).</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            var p = UserPreferences.Current;
            return OllamaUrl.Trim()      != p.OllamaBaseUrl                             ||
                   VisionModel.Trim()    != p.VisionModelName                           ||
                   ClipModelsPath.Trim() != (p.ClipModelsPath ?? AppSettings.ModelsPath) ||
                   IsExpressMode         != p.IsExpressMode                             ||
                   MaxThreads            != p.MaxParallelThreads                        ||
                   HighQualityThumbnails != p.HighQualityThumbnails                     ||
                   UseImperialUnits      != p.UseImperialUnits                          ||
                   EnableFaceDetection   != p.EnableFaceDetection                          ||
                   VisionImageSize       != p.VisionImageSize                             ||
                   ChatModel.Trim()      != p.ChatModelName;
        }
    }

    [ObservableProperty] private bool _savedToastVisible;

    public MetricsViewModel Metrics { get; }

    public SettingsViewModel(IExclusionRepository exclusionRepo, OllamaClient ollamaClient,
                             IServiceScopeFactory scopeFactory, MetricsViewModel metrics)
    {
        _exclusionRepo = exclusionRepo;
        _ollamaClient  = ollamaClient;
        _scopeFactory  = scopeFactory;
        Metrics        = metrics;
        var prefs = UserPreferences.Current;
        _ollamaUrl             = prefs.OllamaBaseUrl;
        _visionModel           = prefs.VisionModelName;
        _chatModel             = prefs.ChatModelName;
        _clipModelsPath        = prefs.ClipModelsPath ?? AppSettings.ModelsPath;
        _isExpressMode         = prefs.IsExpressMode;
        _maxThreads            = prefs.MaxParallelThreads;
        _highQualityThumbnails = prefs.HighQualityThumbnails;
        _useImperialUnits      = prefs.UseImperialUnits;
        _enableFaceDetection   = prefs.EnableFaceDetection;
        _visionImageSize       = prefs.VisionImageSize;
        _showTipsOnStartup     = prefs.TipsEnabled;
        _origOllamaUrl      = _ollamaUrl;
        _origVisionModel    = _visionModel;
        _origClipModelsPath = _clipModelsPath;
        _ = LoadExclusionsAsync();
        _ = LoadAvailableModelsAsync();
        foreach (var e in prefs.ExternalEditors)
            ExternalEditors.Add(e);
    }

    /// <summary>
    /// Fetches the installed model list from Ollama and populates <see cref="AvailableModels"/>.
    /// Uses a short-lived client so the current OllamaUrl field is honoured, even if the user
    /// has edited it since the singleton client was created.
    /// </summary>
    private async Task LoadAvailableModelsAsync()
    {
        try
        {
            using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url           = OllamaUrl.TrimEnd('/') + "/api/tags";
            using var resp    = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return;
            var json          = await resp.Content.ReadAsStringAsync();
            var tags          = System.Text.Json.JsonSerializer.Deserialize<TagsResponse>(json, _jsonOpts);
            if (tags?.Models is { Length: > 0 })
            {
                AvailableModels.Clear();
                foreach (var m in tags.Models)
                    AvailableModels.Add(m.Name);
            }
        }
        catch { /* Ollama unreachable — user can still type a model name manually */ }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts = new()
        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
    private record TagsResponse(ModelEntry[]? Models);
    private record ModelEntry(string Name);

    private async Task LoadExclusionsAsync()
    {
        var rules = await _exclusionRepo.GetAllAsync();
        Exclusions.Clear();
        foreach (var r in rules)
            Exclusions.Add(new ExclusionEntry(r));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var prefs    = UserPreferences.Current;
        var clipPath = ClipModelsPath.Trim();
        prefs.OllamaBaseUrl         = OllamaUrl.Trim();
        prefs.VisionModelName       = VisionModel.Trim();
        prefs.ClipModelsPath        = clipPath == AppSettings.ModelsPath ? null : clipPath;
        prefs.IsExpressMode         = IsExpressMode;
        prefs.MaxParallelThreads    = MaxThreads;
        prefs.HighQualityThumbnails = HighQualityThumbnails;
        prefs.UseImperialUnits      = UseImperialUnits;
        prefs.EnableFaceDetection   = EnableFaceDetection;
        prefs.VisionImageSize       = VisionImageSize;
        prefs.ChatModelName         = ChatModel.Trim();
        prefs.ExternalEditors       = [..ExternalEditors];
        prefs.Save();

        OnPropertyChanged(nameof(HasUnsavedChanges));

        SavedToastVisible = true;
        await Task.Delay(2000);
        SavedToastVisible = false;
    }

    [RelayCommand]
    private void AddExternalEditor()
    {
        var name = NewEditorName.Trim();
        var path = NewEditorPath.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return;
        if (!System.IO.File.Exists(path)) return;
        ExternalEditors.Add(new ExternalEditorEntry(name, path));
        NewEditorName = "";
        NewEditorPath = "";
    }

    [RelayCommand]
    private void RemoveExternalEditor(ExternalEditorEntry entry) => ExternalEditors.Remove(entry);

    [RelayCommand]
    private void BrowseExternalEditor()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select editor executable",
            Filter = "Executables (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        NewEditorPath = dlg.FileName;
        // Pre-fill name from exe filename if not set yet.
        if (string.IsNullOrWhiteSpace(NewEditorName))
            NewEditorName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionStatus    = "Testing…";
        ConnectionOk        = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync(OllamaUrl.TrimEnd('/') + "/api/tags");
            ConnectionOk     = resp.IsSuccessStatusCode;
            ConnectionStatus = resp.IsSuccessStatusCode ? "Connected" : $"HTTP {(int)resp.StatusCode}";
            if (resp.IsSuccessStatusCode)
                await LoadAvailableModelsAsync();
        }
        catch (TaskCanceledException)
        {
            ConnectionStatus = "Connection timed out — is Ollama running?";
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("refused") || ex.Message.Contains("actively refused") || ex.Message.Contains("No connection"))
        {
            ConnectionStatus = "Cannot connect — make sure Ollama is running";
        }
        catch (HttpRequestException)
        {
            ConnectionStatus = "Connection failed — check the URL and try again";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Unexpected error: {ex.Message}";
        }
        finally { IsTestingConnection = false; }
    }

    [RelayCommand]
    private async Task AddExclusionAsync()
    {
        var trimmed = NewExclusion.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        bool isFullPath = trimmed.Contains(System.IO.Path.DirectorySeparatorChar)
                       || trimmed.Contains(System.IO.Path.AltDirectorySeparatorChar)
                       || (trimmed.Length >= 2 && trimmed[1] == ':');

        if (Exclusions.Any(e => e.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        // Optimistic update — show immediately in the list and clear the field.
        var rule  = new ExclusionRule { Value = trimmed, IsFullPath = isFullPath };
        var entry = new ExclusionEntry(rule);
        Exclusions.Add(entry);
        NewExclusion        = "";
        ExclusionStatusText = isFullPath ? "Saving and scanning library…" : "Saving…";

        // All DB work on a background thread with its own fresh scope/context so the
        // UI thread is never blocked by SQLite busy waits from the vision worker.
        try
        {
            int removed = await Task.Run(async () =>
            {
                using var scope        = _scopeFactory.CreateScope();
                var exclusionRepo      = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
                await exclusionRepo.AddAsync(rule);  // populates rule.Id via EF change tracking
                if (!isFullPath) return 0;
                var mediaRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                return await mediaRepo.RemoveByFolderAsync(trimmed, includeSubfolders: true);
            });

            if (removed > 0)
            {
                LibraryModified     = true;
                ExclusionStatusText = $"Saved. Removed {removed} photo(s) from library.";
            }
            else
            {
                ExclusionStatusText = "Saved.";
            }
        }
        catch (Exception ex)
        {
            Exclusions.Remove(entry);
            ExclusionStatusText = ex is Microsoft.EntityFrameworkCore.DbUpdateException
                ? "Could not save — database busy. Please try again."
                : $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync(ExclusionEntry entry)
    {
        // Optimistic update — remove from list immediately.
        Exclusions.Remove(entry);
        ExclusionStatusText = "Removing…";

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var repo        = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
                await repo.RemoveAsync(entry.Id);
            });
            ExclusionStatusText = "";
        }
        catch (Exception ex)
        {
            Exclusions.Add(entry);   // rollback
            ExclusionStatusText = ex is Microsoft.EntityFrameworkCore.DbUpdateException
                ? "Could not remove — database busy. Please try again."
                : $"Could not remove: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BrowseExclusionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select folder to exclude" };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(path)
            || Exclusions.Any(e => e.Value.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        var rule  = new ExclusionRule { Value = path, IsFullPath = true };
        var entry = new ExclusionEntry(rule);
        Exclusions.Add(entry);
        ExclusionStatusText = "Saving and scanning library…";

        try
        {
            int removed = await Task.Run(async () =>
            {
                using var scope   = _scopeFactory.CreateScope();
                var exclusionRepo = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
                await exclusionRepo.AddAsync(rule);
                var mediaRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                return await mediaRepo.RemoveByFolderAsync(path, includeSubfolders: true);
            });

            if (removed > 0)
            {
                LibraryModified     = true;
                ExclusionStatusText = $"Saved. Removed {removed} photo(s) from library.";
            }
            else
            {
                ExclusionStatusText = "Saved.";
            }
        }
        catch (Exception ex)
        {
            Exclusions.Remove(entry);
            ExclusionStatusText = ex is Microsoft.EntityFrameworkCore.DbUpdateException
                ? "Could not save — database busy. Please try again."
                : $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowseClipPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select CLIP models folder" };
        if (dialog.ShowDialog() == true)
            ClipModelsPath = dialog.FolderName;
    }

    // ── Maintenance ──────────────────────────────────────────────────────

    /// <summary>
    /// Set to true when the user clicks Rebuild Search Index.
    /// Checked by MainViewModel after the dialog closes to kick off the background rebuild.
    /// </summary>
    public bool RebuildSearchIndexRequested { get; private set; }

    [RelayCommand]
    private void RebuildSearchIndex()
    {
        RebuildSearchIndexRequested = true;
        // Close the Settings window — the rebuild runs in MainViewModel's background
        // so it can show progress in the status bar without blocking the UI.
        foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
        {
            if (w.DataContext == this) { w.Close(); break; }
        }
    }
}
