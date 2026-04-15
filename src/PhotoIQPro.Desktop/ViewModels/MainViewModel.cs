using System.Collections.ObjectModel;
using System.Diagnostics;
using PhotoIQPro.Common;
using static PhotoIQPro.Common.FormatUtilities;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Desktop.Views;
using System.Windows;

namespace PhotoIQPro.Desktop.ViewModels;

public enum GalleryView { AllPhotos, Favorites, Album, People, OnThisDay }

/// <summary>Tag data displayed in the detail panel. Includes Id so remove is possible without a DB lookup.</summary>
public record TagViewModel(Guid Id, string Name, bool IsAIGenerated);

/// <summary>Unit options for the related-images time window.</summary>
public enum RelatedTimeUnit { Minutes, Hours, Days, Weeks, Months }

/// <summary>One entry in the dynamic "Send To" submenu. IsSeparator=true renders as a menu separator.</summary>
public sealed class SendToMenuItem
{
    public string Header { get; }
    public System.Windows.Input.ICommand? Command { get; }
    public object? CommandParameter { get; }
    public bool IsSeparator { get; }

    public SendToMenuItem(bool isSeparator) { IsSeparator = isSeparator; Header = ""; }
    public SendToMenuItem(string header, System.Windows.Input.ICommand? command, object? parameter = null)
    {
        Header = header; Command = command; CommandParameter = parameter;
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    internal static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    // Each DB operation creates its own short-lived scope so that:
    // (a) DbContext change-tracker never accumulates state across the app lifetime, and
    // (b) background tasks (vision worker, heal timer) cannot race with user operations
    //     on a shared non-thread-safe DbContext.
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly IFolderWatcherService  _folderWatcher;

    // Exclusion cache — loaded once at startup, updated by Exclude/Include commands.
    private readonly HashSet<string> _excludedFullPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedNames     = new(StringComparer.OrdinalIgnoreCase);
    public  MetricsViewModel               Metrics    { get; }
    public  OnThisDayViewModel             OnThisDay  { get; } = new();

    [ObservableProperty] private ObservableCollection<MediaFile> _mediaFiles = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindRelatedImagesCommand))]
    private MediaFile? _selectedMediaFile;
    [ObservableProperty] private string _statusText = "Loading library…";
    private bool _initialLoadDone;
    [ObservableProperty] private int _photoCount;
    [ObservableProperty] private int _totalLibraryCount;
    public string PhotoCountText => IsSearchActive
        ? $"{PhotoCount:N0} of {TotalLibraryCount:N0} photos"
        : $"{PhotoCount:N0} photos";

    public bool   ShowExpressLimit   => UserPreferences.Current.IsExpressMode;
    public string ExpressLimitText   =>
        $"Express  ·  {TotalLibraryCount:N0} / {PhotoIQPro.Common.AppSettings.ExpressLibraryLimit:N0}";
    [ObservableProperty] private ObservableCollection<TagViewModel> _selectedTags = [];

    // ── Related images mode ──────────────────────────────────────────────────
    // When active the gallery shows only photos related to RelatedAnchorPhoto.
    // All normal search/sort/view controls continue to work within that filtered set.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    [NotifyPropertyChangedFor(nameof(IsRelatedNoResults))]
    private bool _isRelatedImagesMode;
    [ObservableProperty] private MediaFile? _relatedAnchorPhoto;
    [ObservableProperty] private bool      _relatedTimeEnabled     = true;
    [ObservableProperty] private bool      _relatedLocationEnabled;
    [ObservableProperty] private int       _relatedTimeValue       = UserPreferences.Current.RelatedTimeValue;
    [ObservableProperty] private int       _relatedTimeUnitIndex   = UserPreferences.Current.RelatedTimeUnitIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelatedDistanceLabel))]
    private int _relatedDistanceStepIndex = UserPreferences.Current.RelatedDistanceStepIndex;

    private readonly HashSet<Guid> _relatedPhotoIds = [];

    private static readonly int[]    _distStepsMeters   = [100, 500, 1000, 5000, 10000, 50000];
    private static readonly string[] _distMetricLabels  = ["100 m", "500 m", "1 km", "5 km", "10 km", "50 km"];
    private static readonly string[] _distImperialLabels = ["330 ft", "0.3 mi", "0.6 mi", "3 mi", "6 mi", "31 mi"];

    /// <summary>Unit names bound to the ComboBox in the related-images banner.</summary>
    public IReadOnlyList<string> RelatedTimeUnitNames { get; } =
        ["Minutes", "Hours", "Days", "Weeks", "Months"];

    public string RelatedDistanceLabel
    {
        get
        {
            var labels = UserPreferences.Current.UseImperialUnits ? _distImperialLabels : _distMetricLabels;
            return labels[Math.Clamp(RelatedDistanceStepIndex, 0, labels.Length - 1)];
        }
    }

    partial void OnRelatedTimeValueChanged(int value)
    {
        UserPreferences.Current.RelatedTimeValue = Math.Max(1, value);
        UserPreferences.Current.Save();
    }

    partial void OnRelatedTimeUnitIndexChanged(int value)
    {
        UserPreferences.Current.RelatedTimeUnitIndex = value;
        UserPreferences.Current.Save();
    }

    partial void OnRelatedDistanceStepIndexChanged(int value)
    {
        UserPreferences.Current.RelatedDistanceStepIndex = value;
        UserPreferences.Current.Save();
    }

    private static int TimeValueToMinutes(int value, RelatedTimeUnit unit) => unit switch
    {
        RelatedTimeUnit.Minutes => value,
        RelatedTimeUnit.Hours   => value * 60,
        RelatedTimeUnit.Days    => value * 1440,
        RelatedTimeUnit.Weeks   => value * 10080,
        RelatedTimeUnit.Months  => value * 43200,
        _                       => value * 60,
    };

    [ObservableProperty] private bool _isFindingRelated;
    private CancellationTokenSource? _relatedCts;

    [RelayCommand]
    private void CancelRelatedSearch() => _relatedCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanFindRelatedImages))]
    private async Task FindRelatedImagesAsync()
    {
        if (SelectedMediaFile == null) return;

        _relatedCts?.Cancel();
        _relatedCts = new CancellationTokenSource();
        var ct = _relatedCts.Token;

        RelatedAnchorPhoto = SelectedMediaFile;
        _relatedPhotoIds.Clear();
        IsFindingRelated = true;
        try
        {
            await RunRelatedQueryAsync(ct);
            ct.ThrowIfCancellationRequested();
            IsRelatedImagesMode = true;
            await LoadAsync();
        }
        catch (OperationCanceledException) { /* user cancelled — stay on current view */ }
        finally { IsFindingRelated = false; }
    }

    private bool CanFindRelatedImages() => SelectedMediaFile != null;

    [RelayCommand]
    private async Task ExitRelatedImagesModeAsync()
    {
        IsRelatedImagesMode = false;
        RelatedAnchorPhoto  = null;
        _relatedPhotoIds.Clear();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SaveRelatedAsAlbumAsync()
    {
        if (!IsRelatedImagesMode || _relatedPhotoIds.Count == 0) return;

        // Build a suggested album name from the anchor photo's date/filename.
        string suggestedName = RelatedAnchorPhoto != null
            ? (RelatedAnchorPhoto.DateTaken.HasValue
                ? $"Photos from {RelatedAnchorPhoto.DateTaken.Value:MMM d, yyyy}"
                : $"Related to {RelatedAnchorPhoto.FileName}")
            : "Related Photos";

        var libraries = await WithLibRepo(r => r.GetAllAsync());
        if (libraries.Count == 0) return;
        var libraryId = libraries[0].Id;

        // Create album
        var album = await WithLibRepo(r => r.CreateAlbumAsync(libraryId, suggestedName));

        // Add all related photos
        var photoIds = _relatedPhotoIds.ToList();
        await WithLibRepo(r => r.AddPhotosToAlbumAsync(album.Id, photoIds));

        // Exit related mode and navigate to the new album
        IsRelatedImagesMode = false;
        RelatedAnchorPhoto  = null;
        _relatedPhotoIds.Clear();
        await LoadSidebarLibrariesAsync();
        NavigateToAlbum(album.Id, suggestedName);

        StatusText = $"Created album \"{suggestedName}\" with {photoIds.Count} photos";
    }

    [RelayCommand]
    private async Task RefreshRelatedImagesAsync()
    {
        if (!IsRelatedImagesMode) return;
        _relatedPhotoIds.Clear();
        await RunRelatedQueryAsync();
        await LoadAsync();
    }

    /// <summary>
    /// Executes the time/location queries and populates <see cref="_relatedPhotoIds"/>.
    /// Does not reload the gallery — callers do that after this returns.
    /// </summary>
    private async Task RunRelatedQueryAsync(CancellationToken ct = default)
    {
        if (RelatedAnchorPhoto == null) return;

        _relatedPhotoIds.Add(RelatedAnchorPhoto.Id);   // anchor always appears, even if query returns nothing

        var timeMinutes = TimeValueToMinutes(Math.Max(1, RelatedTimeValue), (RelatedTimeUnit)RelatedTimeUnitIndex);
        var radiusKm    = _distStepsMeters[Math.Clamp(RelatedDistanceStepIndex, 0, _distStepsMeters.Length - 1)] / 1000.0;

        // Fall back to DateImported when the photo has no EXIF date (e.g. Facebook downloads).
        var anchorDate = RelatedAnchorPhoto.DateTaken ?? RelatedAnchorPhoto.DateImported;
        if (RelatedTimeEnabled)
        {
            ct.ThrowIfCancellationRequested();
            var byTime = await WithRepo(r =>
                r.GetNearbyDateAsync(anchorDate, timeMinutes, RelatedAnchorPhoto.Id, maxResults: 5000));
            foreach (var p in byTime) _relatedPhotoIds.Add(p.Id);
        }

        if (RelatedLocationEnabled && RelatedAnchorPhoto.Latitude  is double lat
                                   && RelatedAnchorPhoto.Longitude is double lon)
        {
            ct.ThrowIfCancellationRequested();
            var byLoc = await WithRepo(r =>
                r.GetNearbyLocationAsync(lat, lon, radiusKm, RelatedAnchorPhoto.Id, maxResults: 5000));
            foreach (var p in byLoc) _relatedPhotoIds.Add(p.Id);
        }
    }

    [ObservableProperty] private string _newTagText = "";
    [ObservableProperty] private GalleryView _activeView = GalleryView.AllPhotos;
    [ObservableProperty] private string _searchQuery = "";
    /// <summary>Text currently typed in the search box. Not committed to <see cref="SearchQuery"/> until the user hits Enter or clicks Search.</summary>
    [ObservableProperty] private string _pendingSearchQuery = "";
    private CancellationTokenSource _loadCts = new();
    [ObservableProperty] private double _thumbnailSize = 180;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSemanticSearch;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDescriptions))]
    [NotifyPropertyChangedFor(nameof(PendingDescriptionText))]
    private int _pendingDescriptionCount;
    public bool HasPendingDescriptions => PendingDescriptionCount > 0;
    public string PendingDescriptionText => $"{PendingDescriptionCount} awaiting AI";

    // Full metadata panel (loaded on demand from disk)
    public ObservableCollection<MetadataTag> AllMetadata { get; } = [];
    private bool _allMetadataLoaded;

    // Album navigation
    [ObservableProperty] private Guid? _activeAlbumId;
    [ObservableProperty] private string _activeAlbumName = "";
    public ObservableCollection<LibraryNodeViewModel> SidebarLibraries { get; } = [];
    public bool HasOfflineFiles => OfflineCount > 0;

    /// <summary>Raised after a bulk exclude to ask the view to scroll a specific photo into view.</summary>
    public event Action<MediaFile>? ScrollIntoViewRequested;
    public string OfflineStatusText => $"{OfflineCount} photo{(OfflineCount == 1 ? "" : "s")} on offline drives";
    public bool SelectedIsOffline => SelectedMediaFile?.IsOffline ?? false;
    public bool SelectedFolderIsExcluded
    {
        get
        {
            var path = SelectedMediaFile?.FilePath == null ? null
                       : Path.GetDirectoryName(SelectedMediaFile.FilePath);
            if (path == null) return false;
            if (_excludedFullPaths.Contains(path)) return true;
            var name = Path.GetFileName(path);
            return !string.IsNullOrEmpty(name) && _excludedNames.Contains(name);
        }
    }
    [ObservableProperty] private System.Windows.Shell.TaskbarItemProgressState _taskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    [ObservableProperty] private double _taskbarProgressValue;

    // ── Multi-selection ──────────────────────────────────────────────────────
    public ObservableCollection<MediaFile> MultiSelectedItems { get; } = [];
    private MediaFile? _selectionAnchor;
    // Increments on every collection change so IsInCollection MultiBindings re-evaluate.
    // ObservableCollection never raises PropertyChanged for the property itself, only
    // CollectionChanged — so bindings that need to react to membership changes need this.
    public int MultiSelectedVersion { get; private set; }
    public int SelectedCount => MultiSelectedItems.Count;
    public bool HasAnyMultiSelected => MultiSelectedItems.Count > 0;
    public bool HasMultiSelection => MultiSelectedItems.Count > 1;
    public string ReanalyzeMultiSelectedLabel  => $"Re-analyze selected ({SelectedCount})";
    public string ExcludeImageLabel => HasMultiSelection
        ? $"Exclude {SelectedCount} Images" : "Exclude this Image";
    public string RemoveFromLibraryLabel => HasMultiSelection
        ? $"Remove {SelectedCount} from Library" : "Remove from Library";
    public string DeletePermanentlyLabel => HasMultiSelection
        ? $"Delete {SelectedCount} Photos Permanently" : "Delete Permanently";

    public bool HasAnySelected      => HasAnyMultiSelected || SelectedMediaFile != null;
    public bool IsReanalyzeSelectedEnabled => !SelectedIsOffline;
    // ── Person / tag filter mode ─────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _isPersonFilterActive;

    /// <summary>Photo IDs for the active person filter. Empty when <see cref="IsPersonFilterActive"/> is false.</summary>
    private IReadOnlyList<Guid> _personFilterPhotoIds = [];

    [ObservableProperty] private string _activeFilterLabel = "";

    [RelayCommand]
    private async Task ClearPersonFilterAsync()
    {
        IsPersonFilterActive  = false;
        ActiveFilterLabel     = "";
        _personFilterPhotoIds = [];
        await LoadAsync();
    }

    public bool IsLibraryEmpty      => PhotoCount == 0 && string.IsNullOrWhiteSpace(SearchQuery) && !IsRelatedImagesMode && !IsPersonFilterActive;
    public bool HasNoResults        => PhotoCount == 0 && !string.IsNullOrWhiteSpace(SearchQuery) && !IsRelatedImagesMode && !IsPersonFilterActive;
    public bool IsRelatedNoResults  => IsRelatedImagesMode && PhotoCount == 0;
    public bool IsSearchActive               => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsSemanticAvailable          => _semanticSearch.IsModelAvailable;
    public bool ShowSemanticUnavailableHint  => IsSearchActive && !IsSemanticAvailable;
    public string NoResultsMessage  => $"No photos found for \"{SearchQuery}\"";
    // Legacy alias kept for any existing bindings
    public bool IsEmpty => PhotoCount == 0;
    public bool HasSelection => SelectedMediaFile != null;
    // null  = description never attempted (video, or photo not yet analyzed)
    // ""    = attempted but model returned nothing / preprocessor failed
    // value = real description
    public bool HasAiDescription             => !string.IsNullOrEmpty(SelectedMediaFile?.AiDescription);
    public bool DescriptionAttemptedButFailed => SelectedMediaFile?.AiDescription == string.Empty
                                                 && SelectedMediaFile?.UserDescription == null;
    public bool HasSelectedTags => SelectedTags.Count > 0;

    // ADR-003 — description display + inline editing
    // Direct observable properties — avoids computed-chain binding refresh issues.
    [ObservableProperty] private string _displayDescription    = "";
    [ObservableProperty] private bool   _hasDisplayDescription;
    [ObservableProperty] private bool   _hasUserDescription;
    [ObservableProperty] private string _descriptionLabel      = "AI DESCRIPTION";
    [ObservableProperty] private bool   _isEditingDescription;
    [ObservableProperty] private string _editingDescriptionText = "";
    [ObservableProperty] private string _descriptionModelLine  = "";
    [ObservableProperty] private bool   _hasDescriptionModelLine;

    // ADR-008 — caption display + inline editing
    [ObservableProperty] private string _displayCaption     = "";
    [ObservableProperty] private bool   _hasDisplayCaption;
    [ObservableProperty] private bool   _isEditingCaption;
    [ObservableProperty] private string _editingCaptionText = "";

    // Description freshness (prompt version + model tracking)
    [ObservableProperty] private bool   _isDescriptionCurrent;
    [ObservableProperty] private bool   _isDescriptionStale;
    [ObservableProperty] private string _descriptionFreshnessTooltip = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutdatedDescriptions))]
    private int _outdatedDescriptionCount;
    public bool HasOutdatedDescriptions => OutdatedDescriptionCount > 0;

    private void RefreshDescriptionProperties()
    {
        var display = SelectedMediaFile?.UserDescription ?? SelectedMediaFile?.AiDescription ?? "";
        var hasUser = SelectedMediaFile?.UserDescription != null;
        var model   = (!hasUser && !string.IsNullOrEmpty(SelectedMediaFile?.AiModelUsed))
                      ? $"via {SelectedMediaFile!.AiModelUsed}"
                      : "";
        DisplayDescription      = display;
        HasDisplayDescription   = !string.IsNullOrEmpty(display);
        HasUserDescription      = hasUser;
        DescriptionLabel        = hasUser ? "YOUR DESCRIPTION" : "AI DESCRIPTION";
        DescriptionModelLine    = model;
        HasDescriptionModelLine = !string.IsNullOrEmpty(model);

        // Freshness — only meaningful when there is an AI description and no user override.
        var mf = SelectedMediaFile;
        if (!hasUser && mf?.AiDescription != null && mf.AiDescription != string.Empty)
        {
            var currentModel  = UserPreferences.Current.VisionModelName;
            var currentPrompt = AppSettings.CurrentPromptVersion;
            bool modelMatch  = mf.AiModelUsed == currentModel;
            bool promptMatch = mf.PromptVersion == currentPrompt;
            bool current     = modelMatch && promptMatch;

            IsDescriptionCurrent = current;
            IsDescriptionStale   = !current;
            DescriptionFreshnessTooltip = current
                ? $"Current — {currentModel} / {currentPrompt}"
                : $"Outdated — generated with {mf.AiModelUsed ?? "unknown model"} / {mf.PromptVersion ?? "unknown prompt"}\nCurrent: {currentModel} / {currentPrompt}\nRe-analyze to update.";
        }
        else
        {
            IsDescriptionCurrent = false;
            IsDescriptionStale   = false;
            DescriptionFreshnessTooltip = "";
        }
    }

    private void RefreshCaptionProperties()
    {
        var caption    = SelectedMediaFile?.UserCaption ?? "";
        DisplayCaption    = caption;
        HasDisplayCaption = !string.IsNullOrEmpty(caption);
    }

    // Derived display strings for the details panel
    public string SelectedDateText       => SelectedMediaFile?.DateTaken?.ToString("MMMM d, yyyy") ?? "";
    public string SelectedTimeText       => SelectedMediaFile?.DateTaken?.ToString("h:mm tt") ?? "";
    public string SelectedDimensionsText => SelectedMediaFile is { Width: > 0, Height: > 0 } mf ? $"{mf.Width} × {mf.Height}" : "";
    public string SelectedFileSizeText   => SelectedMediaFile != null ? FormatFileSize(SelectedMediaFile.FileSize) : "";
    public bool   SelectedIsFavorite     => SelectedMediaFile?.IsFavorite ?? false;

    // EXIF exposure fields
    public string SelectedApertureText    => SelectedMediaFile?.Aperture    is double ap  ? $"f/{ap:F1}"  : "";
    public string SelectedShutterText     => SelectedMediaFile?.ShutterSpeed is string s ? $"{s} s" : "";
    public string SelectedIsoText         => SelectedMediaFile?.ISO          is int iso   ? $"ISO {iso}"  : "";
    public string SelectedFocalLengthText => SelectedMediaFile?.FocalLength  is double fl ? $"{fl:F0} mm" : "";
    public bool HasAperture     => SelectedMediaFile?.Aperture     != null;
    public bool HasShutterSpeed => SelectedMediaFile?.ShutterSpeed != null;
    public bool HasIso          => SelectedMediaFile?.ISO          != null;
    public bool HasFocalLength  => SelectedMediaFile?.FocalLength  != null;
    public bool HasExifData     => HasAperture || HasShutterSpeed || HasIso || HasFocalLength;

    // GPS coordinates — formatted as decimal degrees for display; opens Maps on click
    public bool   HasGps        => SelectedMediaFile?.Latitude != null && SelectedMediaFile?.Longitude != null;
    public string SelectedGpsText
    {
        get
        {
            if (SelectedMediaFile is not { Latitude: double lat, Longitude: double lon }) return "";
            var latDir = lat >= 0 ? "N" : "S";
            var lonDir = lon >= 0 ? "E" : "W";
            return $"{Math.Abs(lat):F4}° {latDir},  {Math.Abs(lon):F4}° {lonDir}";
        }
    }

    [RelayCommand]
    private void OpenInMaps()
    {
        if (SelectedMediaFile is not { Latitude: double lat, Longitude: double lon }) return;
        // ms-maps URI opens Windows Maps; browser fallback via default handler for https URIs.
        var uri = $"https://maps.google.com/maps?q={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* ignore if no browser available */ }
    }

    // Vision model displayed in status bar — read from preferences (requires restart to change).
    public string ActiveVisionModel => UserPreferences.Current.IsExpressMode
        ? AppSettings.TierExpress
        : UserPreferences.Current.VisionModelName;

    // Active nav state for sidebar highlighting
    public bool IsAllPhotosActive  => ActiveView == GalleryView.AllPhotos;
    public bool IsFavoritesActive  => ActiveView == GalleryView.Favorites;
    public bool IsAlbumActive      => ActiveView == GalleryView.Album;
    public bool IsPeopleActive     => ActiveView == GalleryView.People;
    public bool IsOnThisDayActive  => ActiveView == GalleryView.OnThisDay;
    public bool IsGalleryVisible   => ActiveView is not GalleryView.OnThisDay;

    public ImportProgressViewModel Progress { get; }

    public MainViewModel(IServiceScopeFactory scopeFactory, MetricsViewModel metrics,
                         ISemanticSearchService semanticSearch, ImportProgressViewModel progress,
                         IFolderWatcherService folderWatcher)
    {
        _scopeFactory   = scopeFactory;
        Metrics         = metrics;
        _semanticSearch = semanticSearch;
        Progress        = progress;
        _folderWatcher  = folderWatcher;
        Progress.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImportProgressViewModel.IsRunning))
            {
                // ReanalyzeSelectedCommand and IsReanalyzeSelectedEnabled no longer depend on
                // IsRunning (CanRunReanalyze = !SelectedIsOffline only) — no notification needed.
                ReanalyzeAllCommand.NotifyCanExecuteChanged();
                ReanalyzeOutdatedCommand.NotifyCanExecuteChanged();
            }
        };
        _ = LoadAsync();
        _ = LoadSidebarLibrariesAsync();
        _ = LoadExclusionCacheAsync();
        // Delay resume so LoadAsync can render the gallery before the import pipeline
        // starts competing for DB and thread-pool resources.
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => ResumeImportQueueAsync(),
            TaskScheduler.Default).Unwrap();
        _ = VisionBackgroundWorkerAsync(_visionWorkerCts.Token);
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => FaceDetectionWorkerAsync(_faceWorkerCts.Token),
            TaskScheduler.Default).Unwrap();
        _ = StartupHealAsync();
        _ = ResumeUpdateOutdatedAsync();
        StartPeriodicHealTimer();
        _ = StartFolderWatcherAsync();
        RebuildSendToMenuItems();
    }

    // ── Scoped repository helpers ────────────────────────────────────────────
    // Creates a fresh DI scope for each DB operation. The scope (and its DbContext)
    // is disposed as soon as the operation completes, keeping the change-tracker clean.

    private async Task<T> WithRepo<T>(Func<IMediaFileRepository, Task<T>> op)
    {
        using var scope = _scopeFactory.CreateScope();
        return await op(scope.ServiceProvider.GetRequiredService<IMediaFileRepository>());
    }

    private async Task WithRepo(Func<IMediaFileRepository, Task> op)
    {
        using var scope = _scopeFactory.CreateScope();
        await op(scope.ServiceProvider.GetRequiredService<IMediaFileRepository>());
    }

    private async Task<T> WithLibRepo<T>(Func<ILibraryRepository, Task<T>> op)
    {
        using var scope = _scopeFactory.CreateScope();
        return await op(scope.ServiceProvider.GetRequiredService<ILibraryRepository>());
    }

    private async Task WithLibRepo(Func<ILibraryRepository, Task> op)
    {
        using var scope = _scopeFactory.CreateScope();
        await op(scope.ServiceProvider.GetRequiredService<ILibraryRepository>());
    }

    /// <summary>
    /// Runs once at startup — finds any photos left in Pending state (Ollama was
    /// down at import time, or app crashed before analysis ran) and re-analyzes them.
    /// Waits for import queue resume to finish first so we don't compete with it.
    /// </summary>
    private async Task StartupHealAsync()
    {
        // Brief delay so the import queue resume has a head start.
        await Task.Delay(TimeSpan.FromSeconds(10));
        await HealUnanalyzedAsync();
    }

    /// <summary>
    /// Called on startup — if the update-outdated flag file exists (left behind when the user
    /// exited mid-run), automatically resumes the operation after the gallery loads.
    /// </summary>
    private async Task ResumeUpdateOutdatedAsync()
    {
        if (!File.Exists(AppSettings.UpdateOutdatedFlagPath)) return;

        // Read the skipUserModified setting that was in effect when the user exited.
        bool skipUserModified = false;
        try
        {
            var text = await File.ReadAllTextAsync(AppSettings.UpdateOutdatedFlagPath);
            skipUserModified = text.Trim() == "1";
        }
        catch { }

        // Wait for startup load and import-queue resume to settle before starting.
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (Progress.IsRunning) return;  // something else grabbed the progress UI

        AttachTaskbar(Progress);
        AttachPhotoCallback(Progress);
        await Progress.RunReanalyzeOutdatedAsync(skipUserModified);
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
    }

    private void StartPeriodicHealTimer()
    {
        _healTimerTick = async (_, _) => await HealUnanalyzedAsync();
        _healTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _healTimer.Tick += _healTimerTick;
        _healTimer.Start();
    }

    /// <summary>
    /// Starts the folder watcher after a brief delay so startup DB migrations and the
    /// initial FTS rebuild complete before the watcher begins querying the library.
    /// </summary>
    private async Task StartFolderWatcherAsync()
    {
        await Task.Delay(3000); // let startup settle
        // InvokeAsync (non-blocking) — the FileSystemWatcher callback runs on a thread-pool thread.
        // Dispatcher.Invoke would block that thread until the UI thread is free, which stalls the
        // watcher event queue and can cause thread-pool starvation under heavy import load.
        _folderWatcher.OnPhotoImported = mf =>
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Re-use the same live-update path as manual imports.
                var existing = MediaFiles.FirstOrDefault(m => m.Id == mf.Id);
                if (existing == null)
                    MediaFiles.Insert(0, mf);
                PhotoCount = MediaFiles.Count;
                StatusText = $"Auto-imported: {mf.FileName}";
            });
        await _folderWatcher.StartAsync();
    }

    public void Dispose()
    {
        if (_healTimer != null)
        {
            _healTimer.Stop();
            if (_healTimerTick != null)
                _healTimer.Tick -= _healTimerTick;
            _healTimer = null;
        }
        _visionWorkerCts.Cancel();
        _faceWorkerCts.Cancel();
        // Do not dispose these CTS instances. Workers hold the tokens and may still be
        // mid-await; disposing while in use throws ObjectDisposedException. Cancel() is
        // sufficient — the workers exit on the next iteration.
        _folderWatcher.Stop();
    }

    /// <summary>
    /// Silently starts analysis on any photos with AnalysisStatus == Pending.
    /// No-ops if an import or analysis is already running.
    /// </summary>
    private async Task HealUnanalyzedAsync()
    {
        if (Progress.IsRunning) return;
        var pending = (await WithRepo(r => r.GetUnanalyzedAsync(limit: int.MaxValue)))
            .Where(m => m.AnalysisStatus == AnalysisStatus.Pending)
            .ToList();
        if (pending.Count == 0) return;

        StatusText = $"Auto-healing {pending.Count} unanalyzed photo(s)…";
        AttachTaskbar(Progress);
        AttachMetrics(Progress);
        AttachPhotoCallback(Progress);
        using (await AcquireAnalysisLockAsync("HealUnanalyzed"))
            await Progress.RunReanalyzeAsync(unanalyzedOnly: true);
        ClearTaskbar();
        Metrics.ClearBatchStatus();
        StatusText = Progress.ResultMessage;
        await LoadAsync();
    }

    // ── Data loading ────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        // Cancel any in-flight load so stale results never overwrite a newer query.
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        MultiSelectedItems.Clear();
        _selectionAnchor = null;
        NotifyMultiSelectChanged();
        IsLoading = true;
        try
        {
            IEnumerable<MediaFile> photos;
            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                // Run keyword (FTS5) and semantic (CLIP) in parallel.
                // FTS is DB-backed and fast; semantic involves ONNX inference and can be slow.
                // Cap the semantic wait so a hung model doesn't block the UI indefinitely.
                var capturedQuery = SearchQuery;
                var ftsTask      = WithRepo(r => r.SearchAsync(capturedQuery));
                var semanticTask = _semanticSearch.SearchAsync(SearchQuery);
                await Task.WhenAll(ftsTask, Task.WhenAny(semanticTask, Task.Delay(TimeSpan.FromSeconds(5), ct)));
                ct.ThrowIfCancellationRequested();

                var ftsResults      = (await ftsTask).ToList();
                var semanticResults = semanticTask.IsCompletedSuccessfully ? semanticTask.Result : [];

                if (semanticResults.Count == 0)
                {
                    // Semantic unavailable or no concept matches — keyword only.
                    photos           = ftsResults;
                    IsSemanticSearch = false;
                }
                else if (ftsResults.Count == 0)
                {
                    // No keyword matches at all — pure concept results.
                    photos           = semanticResults;
                    IsSemanticSearch = true;
                }
                else
                {
                    // Both have results: keyword matches first (intentional), then any
                    // concept-only extras the keyword search missed.
                    var ftsIds       = ftsResults.Select(m => m.Id).ToHashSet();
                    var conceptExtras = semanticResults.Where(m => !ftsIds.Contains(m.Id)).ToList();
                    photos           = [..ftsResults, ..conceptExtras];
                    IsSemanticSearch = conceptExtras.Count > 0;
                }
            }
            else if (IsRelatedImagesMode)
            {
                // Related-images mode with no search: show only the related set.
                IsSemanticSearch = false;
                photos = _relatedPhotoIds.Count > 0
                    ? await WithRepo(r => r.GetByIdsAsync(_relatedPhotoIds))
                    : [];
            }
            else
            {
                IsSemanticSearch = false;
                photos = ActiveView switch
                {
                    GalleryView.Favorites when true => await WithRepo(r => r.GetFavoritesAsync()),
                    GalleryView.Album when ActiveAlbumId.HasValue =>
                        await WithLibRepo(r => r.GetPhotosByAlbumAsync(ActiveAlbumId.Value)),
                    _ => await WithRepo(r => r.GetAllAsync())
                };
            }

            // In related-images mode with an active search, intersect the search results
            // with the related set so search works within the filtered view.
            if (IsRelatedImagesMode && _relatedPhotoIds.Count > 0 && !string.IsNullOrWhiteSpace(SearchQuery))
                photos = photos.Where(m => _relatedPhotoIds.Contains(m.Id));

            // In person-filter mode, restrict results to photos featuring that person.
            if (IsPersonFilterActive && _personFilterPhotoIds.Count > 0)
            {
                var personIdSet = _personFilterPhotoIds.ToHashSet();
                photos = photos.Where(m => personIdSet.Contains(m.Id));
            }

            ct.ThrowIfCancellationRequested();

            var prevSelectedId = SelectedMediaFile?.Id;
            var photoList = photos is List<MediaFile> pl ? pl : photos.ToList();
            MediaFiles = new ObservableCollection<MediaFile>(photoList);
            PhotoCount = photoList.Count;
            TotalLibraryCount = IsSearchActive ? await WithRepo(r => r.CountAsync()) : PhotoCount;
            OnPropertyChanged(nameof(PhotoCountText));
            OnPropertyChanged(nameof(ExpressLimitText));
            PendingDescriptionCount = photoList.Count(m => m.AnalysisStatus == AnalysisStatus.VisionPending);

            // Thumbnails load on demand via LazyThumbnailBehavior as items scroll into view.
            // No bulk preload — keeps memory bounded to visible items even with 26K+ photos.

            // Restore selection to the fresh DB copy so the detail panel reflects
            // updated tags/description after any operation that ends with LoadAsync.
            if (prevSelectedId.HasValue)
            {
                var fresh = MediaFiles.FirstOrDefault(m => m.Id == prevSelectedId.Value);
                // H-7: if the previously selected photo is no longer in the list (e.g. folder
                // excluded and it was the last photo), clear the detail panel rather than
                // leaving stale data from the old entity.
                SelectedMediaFile = fresh;
            }
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsLibraryEmpty));
            OnPropertyChanged(nameof(HasNoResults));
            OnPropertyChanged(nameof(IsRelatedNoResults));
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(ShowSemanticUnavailableHint));
            OnPropertyChanged(nameof(NoResultsMessage));
        }
        catch (OperationCanceledException)
        {
            // A newer LoadAsync call superseded this one — discard results silently.
            IsLoading = false;
            return;
        }
        finally
        {
            IsLoading = false;
            if (!_initialLoadDone)
            {
                _initialLoadDone = true;
                if (StatusText == "Loading library…")
                    StatusText = $"{PhotoCount:N0} photos";
            }
        }

        // ── Deferred secondary data ──────────────────────────────────────────
        // These queries run after IsLoading=false so the gallery is immediately
        // interactive. They update non-critical UI state (offline badges, outdated
        // count, vision queue) without blocking keyboard/mouse input.
        _ = LoadDeferredAsync(MediaFiles.ToList()).ContinueWith(
            t => AppLog.Error($"LoadDeferredAsync failed: {t.Exception!.InnerException ?? t.Exception}"),
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>
    /// Runs offline detection, the outdated-description count, and vision queue
    /// population after the gallery is already visible and interactive.
    /// Uses a snapshot of the current MediaFiles list; stale results from a previous
    /// load are harmless — the next LoadAsync call will overwrite them.
    /// </summary>
    private async Task LoadDeferredAsync(List<MediaFile> snapshot)
    {
        // Offline check — Directory.Exists per drive root can be slow for disconnected
        // network or removable drives. Run on a thread-pool thread to keep the UI free.
        var offlineCount = await Task.Run(() => MarkOfflineFiles(snapshot));
        OfflineCount = offlineCount;
        OnPropertyChanged(nameof(HasOfflineFiles));
        OnPropertyChanged(nameof(OfflineStatusText));
        OnPropertyChanged(nameof(SelectedIsOffline));

        // Outdated-description count — full table scan without a covering index;
        // queries asynchronously but still holds the method open. Keep deferred.
        OutdatedDescriptionCount = await WithRepo(r => r.CountOutdatedDescriptionsAsync(
            UserPreferences.Current.VisionModelName, AppSettings.CurrentPromptVersion));

        // Queue VisionPending photos for background vision. Fetch only IDs — avoids
        // loading full entities (with all their string fields) just to enqueue GUIDs.
        var pendingIds = await WithRepo(r => r.GetVisionPendingIdsAsync());
        foreach (var id in pendingIds)
            EnqueueForVision(id);
    }

    // ── Background vision worker ─────────────────────────────────────────────
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _visionQueueNormal   = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _visionQueuePriority = new();
    private readonly System.Threading.SemaphoreSlim _visionSignal = new(0);
    // IDs removed from the library this session — vision worker skips them (C-3).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _visionSkipIds = new();
    private readonly System.Threading.CancellationTokenSource _visionWorkerCts    = new();
    private readonly System.Threading.CancellationTokenSource _faceWorkerCts      = new();
    // Prevents concurrent SQLite writes between the background vision worker and
    // any foreground re-analysis command. The worker holds this per photo; re-analysis
    // commands hold it for the entire batch, so they wait for an in-flight vision job
    // to finish before starting and block new vision jobs while running.
    private readonly System.Threading.SemaphoreSlim _analysisLock = new(1, 1);

    /// <summary>
    /// Acquires <see cref="_analysisLock"/> and logs wait/acquire times so we can
    /// detect contention between the background vision worker and foreground commands.
    /// </summary>
    private async Task<IDisposable> AcquireAnalysisLockAsync(string caller, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Info($"[LOCK] {caller}: waiting for _analysisLock (current count={_analysisLock.CurrentCount})");
        await _analysisLock.WaitAsync(ct);
        sw.Stop();
        AppLog.Info($"[LOCK] {caller}: acquired _analysisLock after {sw.ElapsedMilliseconds}ms");
        return new LockReleaser(_analysisLock, caller);
    }

    private sealed class LockReleaser(System.Threading.SemaphoreSlim semaphore, string caller) : IDisposable
    {
        public void Dispose()
        {
            AppLog.Info($"[LOCK] {caller}: releasing _analysisLock");
            semaphore.Release();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _healTimer;
    private EventHandler? _healTimerTick;

    private async Task LoadThumbnailsAsync(MediaFile[] files, CancellationToken ct)
    {
        foreach (var mf in files)
        {
            if (ct.IsCancellationRequested) return;
            var path = mf.ThumbnailMedium ?? mf.ThumbnailSmall;
            if (path == null || !System.IO.File.Exists(path)) continue;

            var bmp = await Task.Run(() =>
            {
                try
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.UriSource        = new Uri(path);
                    b.DecodePixelWidth = 512;
                    b.CacheOption      = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    return (object?)b;
                }
                catch { return null; }
            }, ct);

            if (bmp != null && !ct.IsCancellationRequested)
                mf.LoadedThumbnail = bmp;  // PropertyChanged fires on calling thread (UI thread via await)
        }
    }

    private async Task LoadSingleThumbnailAsync(MediaFile mf)
    {
        var path = mf.ThumbnailMedium ?? mf.ThumbnailSmall;
        if (path == null || !System.IO.File.Exists(path)) return;
        var bmp = await Task.Run(() =>
        {
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.UriSource        = new Uri(path);
                b.DecodePixelWidth = 512;
                b.CacheOption      = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return (object?)b;
            }
            catch { return null; }
        });
        if (bmp != null) mf.LoadedThumbnail = bmp;
    }

    // ── Vision background worker ─────────────────────────────────────────────

    private void EnqueueForVision(Guid photoId)
    {
        if (UserPreferences.Current.IsExpressMode) return;
        _visionQueueNormal.Enqueue(photoId);
        _visionSignal.Release();
    }

    private void PrioritizeVision(Guid photoId)
    {
        if (UserPreferences.Current.IsExpressMode) return;
        _visionQueuePriority.Enqueue(photoId);
        _visionSignal.Release();
    }

    private async Task VisionBackgroundWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _visionSignal.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            // Priority queue takes precedence over normal queue.
            if (!_visionQueuePriority.TryDequeue(out var photoId) &&
                !_visionQueueNormal.TryDequeue(out photoId))
                continue;

            // Skip photos that were deleted or excluded this session (C-3).
            if (_visionSkipIds.ContainsKey(photoId)) continue;

            bool lockAcquired = false;
            try
            {
                var swLock = System.Diagnostics.Stopwatch.StartNew();
                AppLog.Info($"[LOCK] VisionWorker: waiting for _analysisLock (photo {photoId})");
                await _analysisLock.WaitAsync(ct);
                lockAcquired = true;
                AppLog.Info($"[LOCK] VisionWorker: acquired after {swLock.ElapsedMilliseconds}ms (photo {photoId})");
                using var scope = _scopeFactory.CreateScope();
                var import = scope.ServiceProvider.GetRequiredService<IImportService>();
                var repo   = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

                await import.RunVisionForPhotoAsync(photoId, ct);

                var updated = await repo.GetByIdAsync(photoId);
                if (updated != null)
                {
                    // InvokeAsync at ApplicationIdle priority — yields to ALL user input and
                    // render events (including active scrolling) so vision updates never interrupt
                    // the render pipeline.
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var existing = MediaFiles.FirstOrDefault(m => m.Id == photoId);
                        var idx = existing != null ? MediaFiles.IndexOf(existing) : -1;
                        if (idx >= 0)
                        {
                            // Already visible — update in place.
                            var wasSelected = SelectedMediaFile?.Id == photoId;
                            updated.LoadedThumbnail = existing!.LoadedThumbnail;
                            MediaFiles[idx] = updated;
                            if (wasSelected)
                            {
                                SelectedMediaFile = updated;
                                RefreshDescriptionProperties();
                            }
                        }
                        else if (MatchesCurrentView(updated))
                        {
                            // Not currently visible, but the new AI description means it now
                            // matches the active search filter — add it to the gallery.
                            MediaFiles.Add(updated);
                            _ = LoadSingleThumbnailAsync(updated);
                        }
                        PendingDescriptionCount = MediaFiles.Count(m => m.AnalysisStatus == AnalysisStatus.VisionPending);
                    }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                var msg = ex.Message;
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    msg += " -> " + inner.Message;
                AppLog.Error($"Vision worker exception for photo {photoId}: {ex.GetType().Name}: {msg}\n{ex.StackTrace}");
            }
            finally
            {
                if (lockAcquired)
                {
                    AppLog.Info($"[LOCK] VisionWorker: releasing _analysisLock (photo {photoId})");
                    _analysisLock.Release();
                }
            }
        }
    }

    /// <summary>
    /// Background worker that detects faces in photos that haven't been processed yet.
    /// Runs 5 s after startup, processes in small batches, and yields between batches
    /// so it never competes with the UI or the vision worker for CPU/DB resources.
    /// Silently degrades when the FaceDetectionEngine has no models installed.
    /// </summary>
    private async Task FaceDetectionWorkerAsync(CancellationToken ct)
    {
        // Check once whether face detection is even available; if not, exit immediately.
        using (var probe = _scopeFactory.CreateScope())
        {
            var faceService = probe.ServiceProvider.GetRequiredService<IFaceService>();
            if (!faceService.IsAvailable) return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Guid> batch;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                    batch = await repo.GetFaceDetectionPendingIdsAsync(limit: 10);
                }

                if (batch.Count == 0)
                {
                    // Nothing pending — sleep for 30 s then check again (catches newly imported photos).
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                foreach (var photoId in batch)
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        using var scope   = _scopeFactory.CreateScope();
                        var repo          = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                        var faceService   = scope.ServiceProvider.GetRequiredService<IFaceService>();

                        var mf = await repo.GetByIdAsync(photoId);
                        if (mf == null) continue;

                        await faceService.DetectFacesAsync(mf, ct);
                        await repo.UpdateAsync(mf);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        AppLog.Error($"FaceWorker: error on photo {photoId}: {ex.GetType().Name}: {ex.Message}");
                    }

                    // Yield 2 s between photos — face detection is CPU-heavy.
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Error($"FaceWorker batch error: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    partial void OnActiveViewChanged(GalleryView value)
    {
        OnPropertyChanged(nameof(IsAllPhotosActive));
        OnPropertyChanged(nameof(IsFavoritesActive));
        OnPropertyChanged(nameof(IsAlbumActive));
        OnPropertyChanged(nameof(IsPeopleActive));
        OnPropertyChanged(nameof(IsOnThisDayActive));
        OnPropertyChanged(nameof(IsGalleryVisible));
        if (value is not GalleryView.OnThisDay) _ = LoadAsync();
    }

    // ── Navigation commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void ShowAllPhotos()
    {
        PendingSearchQuery = "";
        SearchQuery        = "";
        ActiveAlbumId      = null;
        ActiveAlbumName    = "";
        ActiveView         = GalleryView.AllPhotos;
        SetSidebarActiveAlbum(null);
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        PendingSearchQuery  = "";
        SearchQuery         = "";
        IsRelatedImagesMode = false;
        RelatedAnchorPhoto  = null;
        _relatedPhotoIds.Clear();
        ActiveView          = GalleryView.Favorites;
    }

    [RelayCommand]
    private async Task ShowOnThisDayAsync()
    {
        PendingSearchQuery  = "";
        SearchQuery         = "";
        IsRelatedImagesMode = false;
        RelatedAnchorPhoto  = null;
        _relatedPhotoIds.Clear();
        ActiveView          = GalleryView.OnThisDay;
        var today  = DateTime.Now;
        var photos = await WithRepo(repo => repo.GetOnThisDayAsync(today));
        await OnThisDay.LoadAsync(photos, today);

        // When a memory photo is clicked, jump back to gallery at that photo.
        OnThisDay.PhotoSelected -= OnMemoryPhotoSelected;
        OnThisDay.PhotoSelected += OnMemoryPhotoSelected;

        // When "Save as Album" is clicked on a year group, delegate to MainViewModel.
        OnThisDay.SaveGroupAsAlbumRequested -= OnSaveOnThisDayGroupAsAlbum;
        OnThisDay.SaveGroupAsAlbumRequested += OnSaveOnThisDayGroupAsAlbum;
    }

    private async void OnSaveOnThisDayGroupAsAlbum(OnThisDayGroup group)
        => await SaveOnThisDayGroupAsAlbumAsync(group);

    private void OnMemoryPhotoSelected(MediaFile photo)
    {
        // Return to the full gallery with the photo selected and scrolled into view.
        PendingSearchQuery = "";
        SearchQuery        = "";
        ActiveView         = GalleryView.AllPhotos;
        SelectedMediaFile = MediaFiles.FirstOrDefault(m => m.Id == photo.Id);
        if (SelectedMediaFile != null)
            ScrollIntoViewRequested?.Invoke(SelectedMediaFile);
    }

    [RelayCommand]
    private void ShowOnThisDayPhoto(MediaFile photo) => OnMemoryPhotoSelected(photo);

    [RelayCommand]
    private async Task SaveOnThisDayGroupAsAlbumAsync(OnThisDayGroup? group)
    {
        if (group == null || group.Photos.Count == 0) return;

        var libraries = await WithLibRepo(r => r.GetAllAsync());
        if (libraries.Count == 0) return;

        var suggestedName = $"On This Day — {group.Year}";
        var album = await WithLibRepo(r => r.CreateAlbumAsync(libraries[0].Id, suggestedName));
        var photoIds = group.Photos.Select(p => p.Id).ToList();
        await WithLibRepo(r => r.AddPhotosToAlbumAsync(album.Id, photoIds));

        await LoadSidebarLibrariesAsync();
        NavigateToAlbum(album.Id, suggestedName);
        StatusText = $"Created album \"{suggestedName}\" with {photoIds.Count} photos";
    }

    [RelayCommand]
    private void ShowTags()
    {
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<TagsBrowserWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;

        var vm = (TagsBrowserViewModel)window.DataContext;
        vm.RequestFilterByTag += tagName =>
        {
            window.Close();
            SearchQuery = tagName;
            ActiveView  = GalleryView.AllPhotos;
        };

        window.ShowDialog();
    }

    // ── Album commands ───────────────────────────────────────────────────────

    public void NavigateToAlbum(Guid albumId, string albumName)
    {
        PendingSearchQuery  = "";
        SearchQuery         = "";
        IsRelatedImagesMode = false;
        RelatedAnchorPhoto  = null;
        _relatedPhotoIds.Clear();
        ActiveAlbumId       = albumId;
        ActiveAlbumName     = albumName;
        ActiveView          = GalleryView.Album;
        // M-4: update IsActive on sidebar nodes so the DataTrigger highlights the active album.
        SetSidebarActiveAlbum(albumId);
    }

    private void SetSidebarActiveAlbum(Guid? albumId)
    {
        foreach (var lib in SidebarLibraries)
        {
            lib.IsActive = false;
            foreach (var album in lib.Children)
                album.IsActive = album.Id == albumId;
        }
    }

    [RelayCommand]
    private void NavigateToAlbumSidebar(LibraryNodeViewModel? node)
    {
        if (node?.IsLibrary == false)
            NavigateToAlbum(node.Id, node.Name);
    }

    [RelayCommand]
    private void ShowPeople()
    {
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<PeopleWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;

        // When the user clicks "View photos" for a person, close the People window
        // and filter the gallery to that person's photos.
        var vm = (ViewModels.PeopleViewModel)window.DataContext;
        vm.RequestFilterByPerson += async personId =>
        {
            window.Close();
            await FilterByPersonAsync(personId);
        };

        window.ShowDialog();
    }

    private async Task FilterByPersonAsync(Guid personId)
    {
        var photoIds = await WithRepo(repo => repo.GetFacePhotoIdsForPersonAsync(personId));
        var photos   = await WithRepo(repo => repo.GetByIdsAsync(photoIds));

        _personFilterPhotoIds = photoIds;
        MediaFiles           = new System.Collections.ObjectModel.ObservableCollection<MediaFile>(photos);
        StatusText           = $"{photos.Count} photo(s) featuring this person";
        IsPersonFilterActive = true;
        ActiveFilterLabel    = $"Showing {photos.Count} photo{(photos.Count == 1 ? "" : "s")} with this person";
        ActiveView           = GalleryView.AllPhotos;
    }

    [RelayCommand]
    private async Task ManageLibrariesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<ManageLibrariesWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // If user clicked "View in Gallery" inside the window, navigate to that album.
        if (window.Tag is (Guid albumId, string albumName))
            NavigateToAlbum(albumId, albumName);

        // Refresh sidebar in case libraries/albums were created or deleted.
        await LoadSidebarLibrariesAsync();
    }

    [RelayCommand]
    private async Task RemoveFromAlbumAsync()
    {
        if (!IsAlbumActive || !ActiveAlbumId.HasValue) return;

        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : (List<MediaFile>)[];
        if (targets.Count == 0) return;

        var photoIds = targets.Select(m => m.Id).ToList();
        await WithLibRepo(r => r.RemovePhotosFromAlbumAsync(ActiveAlbumId.Value, photoIds));

        // Remove in-place so the view updates immediately.
        foreach (var m in targets)
            MediaFiles.Remove(m);
        PhotoCount = MediaFiles.Count;

        StatusText = $"Removed {targets.Count} photo{(targets.Count == 1 ? "" : "s")} from \"{ActiveAlbumName}\"";
        await LoadSidebarLibrariesAsync();
    }

    [RelayCommand]
    private async Task AddToAlbumAsync()
    {
        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : (List<MediaFile>)[];
        if (targets.Count == 0) return;

        var libs   = await WithLibRepo(r => r.GetAllAsync());
        var picker = new AlbumPickerWindow(libs) { Owner = System.Windows.Application.Current.MainWindow };
        if (picker.ShowDialog() != true) return;

        Guid   albumId;
        string albumLabel;
        var    photoIds = targets.Select(m => m.Id).ToList();

        if (picker.IsNewAlbum)
        {
            // Create library first if needed.
            Guid libraryId;
            if (picker.NewAlbumLibraryId.HasValue)
            {
                libraryId = picker.NewAlbumLibraryId.Value;
            }
            else
            {
                var newLib = await WithLibRepo(r => r.CreateLibraryAsync(picker.NewLibraryName));
                libraryId  = newLib.Id;
            }
            var newAlbum = await WithLibRepo(r => r.CreateAlbumAsync(libraryId, picker.NewAlbumName));
            albumId    = newAlbum.Id;
            albumLabel = picker.NewAlbumName;
        }
        else
        {
            if (picker.SelectedAlbumId == null) return;
            albumId    = picker.SelectedAlbumId.Value;
            albumLabel = picker.SelectedAlbumName;
        }

        await WithLibRepo(r => r.AddPhotosToAlbumAsync(albumId, photoIds));
        StatusText = $"Added {targets.Count} photo{(targets.Count == 1 ? "" : "s")} to \"{albumLabel}\"";
        await LoadSidebarLibrariesAsync();
    }

    /// <summary>Called from MainWindow drag-drop handler to add dragged photos to an album.</summary>
    public async Task DropPhotosOnAlbumAsync(Guid albumId, string albumName, IEnumerable<Guid> photoIds)
    {
        var ids = photoIds.ToList();
        if (ids.Count == 0) return;
        await WithLibRepo(r => r.AddPhotosToAlbumAsync(albumId, ids));
        StatusText = $"Added {ids.Count} photo{(ids.Count == 1 ? "" : "s")} to \"{albumName}\"";
        await LoadSidebarLibrariesAsync();
    }

    /// <summary>Called when photos are dropped on a library header.
    /// Adds to the single existing album, or auto-creates one when the library has none.</summary>
    public async Task DropPhotosOnLibraryAsync(Guid libraryId, string libraryName, IEnumerable<Guid> photoIds)
    {
        var ids     = photoIds.ToList();
        if (ids.Count == 0) return;

        var libNode = SidebarLibraries.FirstOrDefault(n => n.Id == libraryId);
        var albums  = libNode?.Children.ToList() ?? [];

        Guid   albumId;
        string albumLabel;

        if (albums.Count == 0)
        {
            // No albums yet — create one with the same name as the library.
            var created = await WithLibRepo(r => r.CreateAlbumAsync(libraryId, libraryName));
            albumId    = created.Id;
            albumLabel = libraryName;
        }
        else if (albums.Count == 1)
        {
            albumId    = albums[0].Id;
            albumLabel = albums[0].Name;
        }
        else
        {
            // Multiple albums — add to the first one (user can drag to a specific album next time).
            albumId    = albums[0].Id;
            albumLabel = albums[0].Name;
        }

        await WithLibRepo(r => r.AddPhotosToAlbumAsync(albumId, ids));
        StatusText = $"Added {ids.Count} photo{(ids.Count == 1 ? "" : "s")} to \"{albumLabel}\"";
        await LoadSidebarLibrariesAsync();
    }

    private async Task LoadSidebarLibrariesAsync()
    {
        var counts = await WithLibRepo(r => r.GetAlbumPhotoCountsAsync());
        var libs   = await WithLibRepo(r => r.GetAllAsync());

        SidebarLibraries.Clear();
        foreach (var lib in libs)
        {
            var libNode = new LibraryNodeViewModel(lib);
            foreach (var album in lib.Albums.OrderBy(a => a.Name))
                libNode.Children.Add(new LibraryNodeViewModel(album)
                {
                    PhotoCount = counts.TryGetValue(album.Id, out var c) ? c : 0
                });
            SidebarLibraries.Add(libNode);
        }
    }

    private async Task LoadExclusionCacheAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var repo  = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
        var rules = await repo.GetAllAsync();
        _excludedFullPaths.Clear();
        _excludedNames.Clear();
        foreach (var r in rules)
        {
            if (r.IsFullPath) _excludedFullPaths.Add(r.Value);
            else              _excludedNames.Add(r.Value);
        }
        OnPropertyChanged(nameof(SelectedFolderIsExcluded));
    }

    // ── Import commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Progress.IsRunning) return;
        if (await CheckExpressCeilingAsync()) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title       = "Select folders to import",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true || dlg.FolderNames.Length == 0) return;

        AttachTaskbar(Progress);
        AttachPhotoCallback(Progress);
        AttachExpressLimitCallback(Progress);
        StatusText = "";
        await Progress.RunImportSelectionAsync(dlg.FolderNames, []);
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
        _ = _folderWatcher.RefreshFoldersAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        if (Progress.IsRunning) return;
        if (await CheckExpressCeilingAsync()) return;

        var photoExts = SupportedFormats.PhotoFilterSpec;
        var rawExts   = SupportedFormats.RawFilterSpec;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Select photos to import",
            Multiselect = true,
            Filter      = $"All Photos ({photoExts};{rawExts})|{photoExts};{rawExts}" +
                          $"|Photos ({photoExts})|{photoExts}" +
                          $"|RAW Files ({rawExts})|{rawExts}",
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        AttachTaskbar(Progress);
        AttachPhotoCallback(Progress);
        AttachExpressLimitCallback(Progress);
        StatusText = "";
        await Progress.RunImportSelectionAsync([], dlg.FileNames);
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
        _ = _folderWatcher.RefreshFoldersAsync();
        await LoadAsync();
    }

    // ── ADR-003 — Description editing ───────────────────────────────────────

    [RelayCommand]
    private void BeginEditDescription()
    {
        if (SelectedMediaFile == null) return;
        EditingDescriptionText = DisplayDescription;
        IsEditingDescription = true;
    }

    [RelayCommand]
    private async Task SaveDescriptionAsync()
    {
        if (SelectedMediaFile == null) return;
        var trimmed = EditingDescriptionText.Trim();
        var newDescription = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        // Fresh scope — avoids EF Core identity-map conflicts with the long-lived context.
        var photoId = SelectedMediaFile.Id;
        using (var scope = _scopeFactory.CreateScope())
        {
            var freshRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
            var entity = await freshRepo.GetByIdAsync(photoId);
            if (entity != null)
            {
                entity.UserDescription     = newDescription;
                entity.DescriptionEditedAt = DateTime.UtcNow;
                await freshRepo.UpdateAsync(entity);
            }
        }

        // Sync in-memory object and refresh UI before DB round-trip is even needed.
        SelectedMediaFile.UserDescription     = newDescription;
        SelectedMediaFile.DescriptionEditedAt = DateTime.UtcNow;
        IsEditingDescription = false;  // triggers RefreshDescriptionProperties
    }

    [RelayCommand]
    private void CancelEditDescription()
    {
        IsEditingDescription = false;
    }

    // ── ADR-008 — Caption editing ────────────────────────────────────────────

    [RelayCommand]
    private void BeginEditCaption()
    {
        if (SelectedMediaFile == null) return;
        EditingCaptionText = DisplayCaption;
        IsEditingCaption = true;
    }

    [RelayCommand]
    private async Task SaveCaptionAsync()
    {
        if (SelectedMediaFile == null) return;
        var raw     = EditingCaptionText;
        var trimmed = raw.Trim();
        var caption = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        var photoId = SelectedMediaFile.Id;
        using (var scope = _scopeFactory.CreateScope())
        {
            var freshRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
            var entity = await freshRepo.GetByIdAsync(photoId);
            if (entity != null)
            {
                entity.UserCaptionRaw  = string.IsNullOrEmpty(raw) ? null : raw;
                entity.UserCaption     = caption;
                entity.CaptionEditedAt = DateTime.UtcNow;
                await freshRepo.UpdateAsync(entity);
            }
        }

        SelectedMediaFile.UserCaptionRaw  = string.IsNullOrEmpty(raw) ? null : raw;
        SelectedMediaFile.UserCaption     = caption;
        SelectedMediaFile.CaptionEditedAt = DateTime.UtcNow;
        IsEditingCaption = false;  // triggers RefreshCaptionProperties
    }

    [RelayCommand]
    private void CancelEditCaption()
    {
        IsEditingCaption = false;
    }

    // ── Re-analyze ───────────────────────────────────────────────────────────

    private bool CanRunReanalyze() => !SelectedIsOffline;

    [RelayCommand(CanExecute = nameof(CanRunReanalyze))]
    private async Task ReanalyzeSelectedAsync()
    {
        if (SelectedMediaFile == null) return;

        // If the photo is already in the vision queue, just push it to the front —
        // no need to acquire the analysis lock or interrupt any running import.
        if (SelectedMediaFile.IsVisionPending)
        {
            PrioritizeVision(SelectedMediaFile.Id);
            StatusText = $"Moved \"{SelectedMediaFile.FileName}\" to front of analysis queue";
            return;
        }

        // Full re-analysis (CLIP + vision) requires exclusive access to the pipeline.
        if (Progress.IsRunning)
        {
            StatusText = "Analysis is already running — wait for it to finish or cancel it first";
            return;
        }

        if (SelectedMediaFile.UserDescription != null)
        {
            var warn = System.Windows.MessageBox.Show(
                $"You've edited the AI description for \"{SelectedMediaFile.FileName}\".\n\n" +
                "Re-analyzing will replace your custom description with a new AI-generated one.\n\nContinue?",
                "Overwrite Your Edit?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (warn != System.Windows.MessageBoxResult.Yes) return;
        }

        AttachTaskbar(Progress);
        var photoId = SelectedMediaFile.Id;
        var fileName = SelectedMediaFile.FileName;
        using (await AcquireAnalysisLockAsync("ReanalyzeOne"))
            await Progress.RunReanalyzeOneAsync(photoId, fileName);
        ClearTaskbar();
        // Reload via a fresh scope — the main repo's DbContext has the old entity in its
        // identity map and will return stale data. A new scope gives a clean context.
        using var scope = _scopeFactory.CreateScope();
        var freshRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var updated = await freshRepo.GetByIdAsync(photoId);
        if (updated != null)
        {
            var idx = MediaFiles.IndexOf(SelectedMediaFile);
            if (idx >= 0) MediaFiles[idx] = updated;
            SelectedMediaFile = updated;

            // Reload thumbnail for the swapped entity — LoadedThumbnail is [NotMapped]
            // so it starts null on any fresh DB object. The background task ran over the
            // old array and will never touch this new instance.
            var thumbPath = updated.ThumbnailMedium ?? updated.ThumbnailSmall;
            if (thumbPath != null && System.IO.File.Exists(thumbPath))
            {
                var bmp = await Task.Run(() =>
                {
                    try
                    {
                        var b = new System.Windows.Media.Imaging.BitmapImage();
                        b.BeginInit();
                        b.UriSource        = new Uri(thumbPath);
                        b.DecodePixelWidth = 512;
                        b.CacheOption      = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        return (object?)b;
                    }
                    catch { return null; }
                });
                if (bmp != null) updated.LoadedThumbnail = bmp;
            }
        }
        StatusText = Progress.ResultMessage;
    }

    [RelayCommand(CanExecute = nameof(CanRunReanalyze))]
    private async Task ReanalyzeAllAsync()
    {
        if (Progress.IsRunning) return;
        // M-8: use total library count, not PhotoCount which reflects the current filtered view.
        var totalCount = await WithRepo(r => r.CountAsync());
        var result = System.Windows.MessageBox.Show(
            $"Re-analyze all {totalCount} photos with AI?\n\nThis will replace existing tags and descriptions. It may take a while.",
            "Re-analyze All",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        bool skipUserModified = false;
        int userModifiedCount = MediaFiles.Count(m => m.UserDescription != null);
        if (userModifiedCount > 0)
        {
            var skipResult = System.Windows.MessageBox.Show(
                $"{userModifiedCount} photo{(userModifiedCount == 1 ? " has" : "s have")} a user-edited description.\n\n" +
                "Re-analyze those too?\n\n" +
                "• Yes — re-analyze all photos (user edits will be overwritten)\n" +
                "• No  — skip photos with user-edited descriptions",
                "User-Edited Descriptions",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            skipUserModified = skipResult == System.Windows.MessageBoxResult.No;
        }

        AttachTaskbar(Progress);
        AttachMetrics(Progress);
        AttachPhotoCallback(Progress);
        using (await AcquireAnalysisLockAsync("ReanalyzeAll"))
            await Progress.RunReanalyzeAsync(unanalyzedOnly: false, skipUserModified: skipUserModified);
        ClearTaskbar();
        Metrics.ClearBatchStatus();
        StatusText = Progress.ResultMessage;
        await LoadAsync();
    }

    // ── Multi-select commands ────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSelection(MediaFile? file)
    {
        if (file == null) return;
        if (MultiSelectedItems.Contains(file))
            MultiSelectedItems.Remove(file);
        else
        {
            MultiSelectedItems.Add(file);
            _selectionAnchor = file;
        }
        NotifyMultiSelectChanged();
    }

    // Shift+click: select all items between the last anchor and the target.
    [RelayCommand]
    private void SelectRange(MediaFile? target)
    {
        if (target == null) return;
        if (_selectionAnchor == null) { ToggleSelection(target); return; }

        var anchorIdx = MediaFiles.IndexOf(_selectionAnchor);
        var targetIdx = MediaFiles.IndexOf(target);
        if (anchorIdx < 0 || targetIdx < 0) { ToggleSelection(target); return; }

        var from = Math.Min(anchorIdx, targetIdx);
        var to   = Math.Max(anchorIdx, targetIdx);
        for (int i = from; i <= to; i++)
            if (!MultiSelectedItems.Contains(MediaFiles[i]))
                MultiSelectedItems.Add(MediaFiles[i]);
        NotifyMultiSelectChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var f in MediaFiles)
            if (!MultiSelectedItems.Contains(f))
                MultiSelectedItems.Add(f);
        _selectionAnchor = MediaFiles.LastOrDefault();
        NotifyMultiSelectChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        MultiSelectedItems.Clear();
        _selectionAnchor = null;
        NotifyMultiSelectChanged();
    }

    /// <summary>Commits the pending search box text and executes the search.</summary>
    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        SearchQuery = PendingSearchQuery;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SearchByTagAsync(string tag)
    {
        PendingSearchQuery = tag;
        SearchQuery        = tag;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        PendingSearchQuery = "";
        SearchQuery        = "";
        await LoadAsync();
    }

    private void NotifyMultiSelectChanged()
    {
        MultiSelectedVersion++;
        OnPropertyChanged(nameof(MultiSelectedVersion));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasAnyMultiSelected));
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(HasAnySelected));
        OnPropertyChanged(nameof(ReanalyzeMultiSelectedLabel));
        OnPropertyChanged(nameof(ExcludeImageLabel));
        OnPropertyChanged(nameof(RemoveFromLibraryLabel));
        OnPropertyChanged(nameof(DeletePermanentlyLabel));
        ReanalyzeMultiSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Called on startup — if the import queue file exists (left behind by a crash or
    /// interrupted session), automatically resumes draining it.
    /// </summary>
    private async Task ResumeImportQueueAsync()
    {
        var count = await Progress.GetQueueCountAsync();
        if (count == 0) return;

        // Ask the user — don't silently start a potentially long operation.
        var result = System.Windows.Application.Current.Dispatcher.Invoke(() =>
            System.Windows.MessageBox.Show(
                $"{count} import folder{(count == 1 ? "" : "s")} remain from your last session.\n\nResume importing them now?",
                "Resume Import",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Yes));

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            // User declined — clear the queue so it doesn't prompt again next startup.
            await Progress.ClearQueueAsync();
            return;
        }

        AttachTaskbar(Progress);
        AttachPhotoCallback(Progress);
        AttachExpressLimitCallback(Progress);
        await Progress.RunFromQueueAsync();
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
        await LoadAsync();

        // Startup skipped the FTS rebuild because this queue was pending (to avoid
        // holding SQLite's write lock while imports were running). Rebuild now that
        // the queue is drained so full-text search reflects everything that was imported.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                await repo.RebuildFtsIndexAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Post-resume FTS rebuild failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Returns true if the user is on the Express tier and has reached the library limit,
    /// and shows the upgrade prompt in that case. Callers should abort the import if true.
    /// </summary>
    private async Task<bool> CheckExpressCeilingAsync()
    {
        if (!UserPreferences.Current.IsExpressMode) return false;
        var count = await WithRepo(r => r.CountAsync());
        if (count < AppSettings.ExpressLibraryLimit) return false;

        var dlg = new PhotoIQPro.Desktop.Views.UpgradePromptWindow(count);
        dlg.Owner = System.Windows.Application.Current.MainWindow;
        dlg.ShowDialog();
        return true;
    }

    /// <summary>Returns the currently active/focused window, falling back to MainWindow.</summary>
    private static System.Windows.Window GetActiveWindow() =>
        System.Windows.Application.Current.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current.MainWindow;

    private void AttachTaskbar(ImportProgressViewModel vm)
    {
        StatusText = ""; // clear any previous error/result before the new task begins
        vm.TaskbarProgress = v => { TaskbarProgressValue = v; TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal; };
    }

    private void AttachMetrics(ImportProgressViewModel vm)
        => vm.OnBatchTick = text => Metrics.BatchStatusText = text;

    private void AttachPhotoCallback(ImportProgressViewModel vm)
    {
        vm.OnPhotoAnalyzed = analyzed =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var existing = MediaFiles.FirstOrDefault(m => m.Id == analyzed.Id);
                if (existing != null)
                {
                    var idx = MediaFiles.IndexOf(existing);
                    if (idx >= 0)
                    {
                        analyzed.LoadedThumbnail = existing.LoadedThumbnail;
                        MediaFiles[idx] = analyzed;
                        if (SelectedMediaFile?.Id == analyzed.Id) SelectedMediaFile = analyzed;
                    }
                }
                else if (MatchesCurrentView(analyzed))
                {
                    // New photo imported — only add if it would appear under the current view/filter.
                    MediaFiles.Add(analyzed);
                    PhotoCount = MediaFiles.Count;
                    OnPropertyChanged(nameof(IsEmpty));
                    OnPropertyChanged(nameof(IsLibraryEmpty));
                    _ = LoadSingleThumbnailAsync(analyzed);
                }

                // Queue for background vision if CLIP-only pass was used.
                if (analyzed.AnalysisStatus == AnalysisStatus.VisionPending)
                    EnqueueForVision(analyzed.Id);
            });
        };
    }

    private void AttachExpressLimitCallback(ImportProgressViewModel vm)
    {
        vm.OnExpressLimitReached = importedThisSession =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(async () =>
            {
                // Get the real current count for the dialog.
                int currentCount;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                    currentCount = await repo.CountAsync();
                }
                var win = new UpgradePromptWindow(currentCount)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                win.ShowDialog();
            });
        };
    }

    private void ClearTaskbar()
        => TaskbarProgressState = System.Windows.Shell.TaskbarItemProgressState.None;

    [RelayCommand(CanExecute = nameof(HasMultiSelection))]
    private async Task ReanalyzeMultiSelectedAsync()
    {
        var items = MultiSelectedItems.ToList();
        if (items.Count == 0 || Progress.IsRunning) return;

        int userModifiedCount = items.Count(m => m.UserDescription != null);
        if (userModifiedCount > 0)
        {
            var warn = System.Windows.MessageBox.Show(
                $"{userModifiedCount} of the selected photo{(userModifiedCount == 1 ? "" : "s")} " +
                $"ha{(userModifiedCount == 1 ? "s" : "ve")} a user-edited description.\n\n" +
                "Re-analyzing will replace those edits with new AI-generated descriptions.\n\nContinue?",
                "Overwrite Your Edits?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (warn != System.Windows.MessageBoxResult.Yes) return;
        }

        AttachTaskbar(Progress);
        using (await AcquireAnalysisLockAsync("ReanalyzeSelected"))
            await Progress.RunReanalyzeSelectedAsync(items);
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
        await LoadAsync();
        ClearSelection();
    }


    [RelayCommand(CanExecute = nameof(CanRunReanalyze))]
    private async Task ReanalyzeOutdatedAsync()
    {
        if (Progress.IsRunning) return;

        bool skipUserModified = false;
        int userModifiedCount = MediaFiles.Count(m => m.UserDescription != null);
        if (userModifiedCount > 0)
        {
            var skipResult = System.Windows.MessageBox.Show(
                $"{userModifiedCount} photo{(userModifiedCount == 1 ? " has" : "s have")} a user-edited description.\n\n" +
                "Update those too?\n\n" +
                "• Yes — update all outdated photos (user edits will be overwritten)\n" +
                "• No  — skip photos with user-edited descriptions",
                "User-Edited Descriptions",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            skipUserModified = skipResult == System.Windows.MessageBoxResult.No;
        }

        AttachTaskbar(Progress);
        AttachMetrics(Progress);
        AttachPhotoCallback(Progress);
        using (await AcquireAnalysisLockAsync("ReanalyzeOutdated"))
            await Progress.RunReanalyzeOutdatedAsync(skipUserModified: skipUserModified);
        ClearTaskbar();
        Metrics.ClearBatchStatus();
        StatusText = Progress.ResultMessage;
        OutdatedDescriptionCount = 0; // will be recalculated on next LoadAsync
        await LoadAsync();
    }

    [RelayCommand]
    private void NavigatePrevious()
    {
        if (SelectedMediaFile == null && MediaFiles.Count > 0) { SelectedMediaFile = MediaFiles[0]; return; }
        var idx = MediaFiles.IndexOf(SelectedMediaFile!);
        if (idx > 0) SelectedMediaFile = MediaFiles[idx - 1];
    }

    [RelayCommand]
    private void NavigateNext()
    {
        if (SelectedMediaFile == null && MediaFiles.Count > 0) { SelectedMediaFile = MediaFiles[0]; return; }
        var idx = MediaFiles.IndexOf(SelectedMediaFile!);
        if (idx >= 0 && idx < MediaFiles.Count - 1) SelectedMediaFile = MediaFiles[idx + 1];
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void CopyFullPath()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path != null)
            System.Windows.Clipboard.SetText(path);
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void CopyToFolder()
    {
        var source = SelectedMediaFile?.FilePath;
        if (source == null || !File.Exists(source)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Copy photo to…",
            FileName = Path.GetFileName(source),
            Filter = "All Files|*.*",
            InitialDirectory = Path.GetDirectoryName(source)
        };
        if (dialog.ShowDialog() != true) return;
        var dest = dialog.FileName;
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;
        File.Copy(source, dest, overwrite: false);
        StatusText = $"Copied to {Path.GetFileName(dest)}";
    }

    // ── Send To submenu ──────────────────────────────────────────────────────

    /// <summary>Flat list of items for the dynamic "Send To" submenu (email, print, wallpaper, open-with, external editors).</summary>
    public ObservableCollection<SendToMenuItem> SendToMenuItems { get; } = [];

    private void RebuildSendToMenuItems()
    {
        SendToMenuItems.Clear();
        SendToMenuItems.Add(new SendToMenuItem("Email…",                    SendToEmailCommand));
        SendToMenuItems.Add(new SendToMenuItem("Print",                     SendToPrintCommand));
        SendToMenuItems.Add(new SendToMenuItem("Set as Desktop Wallpaper",  SetAsWallpaperCommand));
        SendToMenuItems.Add(new SendToMenuItem("Open With…",               SendToOpenWithCommand));

        var editors = UserPreferences.Current.ExternalEditors;
        if (editors.Count > 0)
        {
            SendToMenuItems.Add(new SendToMenuItem(isSeparator: true));
            foreach (var e in editors)
                SendToMenuItems.Add(new SendToMenuItem(e.Name, OpenInExternalEditorCommand, e.ExePath));
        }
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void SendToEmail()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path == null) return;
        Process.Start(new System.Diagnostics.ProcessStartInfo($"mailto:?subject=Photo&body={Uri.EscapeDataString(path)}")
            { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void SendToPrint()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = path, Verb = "print", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"SendToPrint failed for '{path}': {ex.GetType().Name}: {ex.Message}");
            StatusText = "No print handler found for this file type.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void SetAsWallpaper()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path == null || !File.Exists(path)) return;
        // SPI_SETDESKWALLPAPER = 0x0014, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE = 3
        NativeMethods.SystemParametersInfo(0x0014, 0, path, 3);
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void SendToOpenWith()
    {
        var path = SelectedMediaFile?.FilePath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            // rundll32 OpenAs_RunDLL always shows the Windows "Open With" picker,
            // even for file types that have no registered association (e.g. .CRW, .ARW).
            // The shell verb "openwith" only works when an association already exists.
            Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe")
                { Arguments = $"shell32.dll,OpenAs_RunDLL \"{path}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error($"SendToOpenWith failed for '{path}': {ex.GetType().Name}: {ex.Message}");
            StatusText = "Could not open the 'Open With' dialog.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanFileOp))]
    private void OpenInExternalEditor(string exePath)
    {
        var path = SelectedMediaFile?.FilePath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
                { Arguments = $"\"{path}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open editor:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanFileOp() => SelectedMediaFile != null && !SelectedIsOffline;

    [RelayCommand]
    private async Task ExcludeFolderAsync()
    {
        if (SelectedMediaFile == null) return;
        var folderPath = Path.GetDirectoryName(SelectedMediaFile.FilePath);
        if (string.IsNullOrEmpty(folderPath)) return;

        var dialog = new Views.ExcludeFolderDialog(folderPath, GetActiveWindow());
        if (dialog.ShowDialog() != true) return;

        var normalizedFolder = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Identify the currently-visible photos that will be removed — before any DB work.
        var toRemove = MediaFiles.Where(mf =>
        {
            var dir = (Path.GetDirectoryName(mf.FilePath) ?? "")
                      .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return dialog.IncludeSubfolders
                ? dir.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
                  dir.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                : dir.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // All DB work on a background thread — avoids UI freeze from SQLite busy waits.
        try
        {
            await Task.Run(async () =>
            {
                using var scope       = _scopeFactory.CreateScope();
                var exclusionRepo     = scope.ServiceProvider.GetRequiredService<PhotoIQPro.Core.Interfaces.IExclusionRepository>();
                var mediaRepo         = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                var rule              = new PhotoIQPro.Core.Models.ExclusionRule { Value = folderPath, IsFullPath = true };
                await exclusionRepo.AddAsync(rule);
                await mediaRepo.RemoveByFolderAsync(folderPath, dialog.IncludeSubfolders);
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not exclude folder: {ex.Message}";
            return;
        }

        _excludedFullPaths.Add(folderPath);
        foreach (var mf in toRemove)
            _visionSkipIds.TryAdd(mf.Id, 0);

        // Remove items in-place — preserves scroll position.
        if (SelectedMediaFile != null && toRemove.Any(m => m.Id == SelectedMediaFile.Id))
            SelectedMediaFile = null;

        foreach (var mf in toRemove)
            MediaFiles.Remove(mf);

        PendingDescriptionCount = MediaFiles.Count(m => m.AnalysisStatus == AnalysisStatus.VisionPending);
        StatusText = $"Folder excluded — {toRemove.Count} photo(s) removed from library.";

        var next = SelectedMediaFile ?? MediaFiles.FirstOrDefault();
        if (next != null) ScrollIntoViewRequested?.Invoke(next);
    }

    [RelayCommand]
    private async Task IncludeFolderAsync()
    {
        if (SelectedMediaFile == null) return;
        var folderPath = Path.GetDirectoryName(SelectedMediaFile.FilePath);
        if (string.IsNullOrEmpty(folderPath)) return;

        using var scope = _scopeFactory.CreateScope();
        var exclusionRepo = scope.ServiceProvider.GetRequiredService<PhotoIQPro.Core.Interfaces.IExclusionRepository>();
        await exclusionRepo.RemoveByValueAsync(folderPath, isFullPath: true);
        _excludedFullPaths.Remove(folderPath);
        OnPropertyChanged(nameof(SelectedFolderIsExcluded));
        StatusText = "Folder included — it will appear in future scans. Re-import to restore removed photos.";
    }

    [RelayCommand]
    private async Task ExcludeImageAsync()
    {
        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : [];
        if (targets.Count == 0) return;

        var msg = targets.Count == 1
            ? $"Exclude \"{targets[0].FileName}\" from the library?\n\nThe file will not be deleted from disk. You can re-import it later."
            : $"Exclude {targets.Count} photos from the library?\n\nThe files will not be deleted from disk. You can re-import them later.";
        var confirm = System.Windows.MessageBox.Show(
            GetActiveWindow(), msg, "Exclude from Library",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        foreach (var mf in targets)
        {
            _visionSkipIds.TryAdd(mf.Id, 0);
            await WithRepo(r => r.ExcludeAsync(mf.Id));
            MediaFiles.Remove(mf);
        }
        MultiSelectedItems.Clear();
        NotifyMultiSelectChanged();
        SelectedMediaFile = null;
        PhotoCount = MediaFiles.Count;
        StatusText = $"Excluded {targets.Count} photo(s) — will not be reimported during future scans";
    }

    [RelayCommand]
    private async Task RemoveFromLibraryAsync()
    {
        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : [];
        if (targets.Count == 0) return;

        var msg = targets.Count == 1
            ? $"Remove \"{targets[0].FileName}\" from the library?\n\nThe file stays on disk, but all tags, descriptions, and album memberships will be lost."
            : $"Remove {targets.Count} photos from the library?\n\nThe files stay on disk, but all tags, descriptions, and album memberships will be lost.";
        var confirm = System.Windows.MessageBox.Show(
            GetActiveWindow(), msg, "Remove from Library",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        foreach (var mf in targets)
        {
            _visionSkipIds.TryAdd(mf.Id, 0);
            await WithRepo(r => r.DeleteAsync(mf.Id));
            MediaFiles.Remove(mf);
        }
        MultiSelectedItems.Clear();
        NotifyMultiSelectChanged();
        SelectedMediaFile = null;
        PhotoCount = MediaFiles.Count;
        StatusText = $"Removed {targets.Count} photo(s) from library";
        _ = LoadSidebarLibrariesAsync();
    }

    [RelayCommand]
    private async Task DeletePermanentlyAsync()
    {
        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : [];
        if (targets.Count == 0) return;

        var msg = targets.Count == 1
            ? $"Permanently delete \"{targets[0].FileName}\" from disk?\n\nThis cannot be undone."
            : $"Permanently delete {targets.Count} photos from disk?\n\nThis cannot be undone.";

        var confirm = System.Windows.MessageBox.Show(
            GetActiveWindow(), msg, "Delete Permanently",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        int deleted = 0, diskFailed = 0;
        foreach (var mf in targets)
        {
            _visionSkipIds.TryAdd(mf.Id, 0);
            try { if (File.Exists(mf.FilePath)) File.Delete(mf.FilePath); }
            catch (Exception ex) { diskFailed++; AppLog.Error($"Failed to delete \"{mf.FilePath}\": {ex.Message}"); }
            await WithRepo(r => r.DeleteAsync(mf.Id));
            MediaFiles.Remove(mf);
            deleted++;
        }
        MultiSelectedItems.Clear();
        NotifyMultiSelectChanged();
        SelectedMediaFile = null;
        PhotoCount = MediaFiles.Count;
        StatusText = diskFailed > 0
            ? $"Removed {deleted} photo{(deleted == 1 ? "" : "s")} from library — {diskFailed} file{(diskFailed == 1 ? "" : "s")} could not be deleted from disk (may be open in another app)"
            : $"Deleted {deleted} photo{(deleted == 1 ? "" : "s")} permanently";
        _ = LoadSidebarLibrariesAsync();
    }

    [RelayCommand]
    private async Task RegenerateThumbnailsAsync()
    {
        if (Progress.IsRunning) return;

        Action<MediaFile> onDone = updated =>
        {
            // Marshal to UI thread — service callback fires on a thread-pool thread.
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var item = MediaFiles.FirstOrDefault(m => m.Id == updated.Id);
                if (item == null) return;
                // Assign paths even if unchanged — MediaFile.Notify() always fires,
                // causing WPF to re-run the converter which loads fresh (IgnoreImageCache).
                item.ThumbnailSmall  = updated.ThumbnailSmall;
                item.ThumbnailMedium = updated.ThumbnailMedium;
                item.ThumbnailLarge  = updated.ThumbnailLarge;
                item.Width           = updated.Width;
                item.Height          = updated.Height;
            });
        };

        AttachTaskbar(Progress);
        await Progress.RunRegenerateThumbnailsAsync(onDone);
        ClearTaskbar();
        StatusText = Progress.ResultMessage;
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenPhotoViewer()
    {
        if (SelectedMediaFile == null) return;
        var idx = MediaFiles.IndexOf(SelectedMediaFile);
        if (idx < 0) return;

        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<PhotoViewerWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        var vm = (PhotoViewerViewModel)window.DataContext;

        // Keep MainViewModel.SelectedMediaFile in sync when the viewer navigates,
        // and propagate the current folder-excluded state for the viewer's context menu.
        vm.PhotoChanged = mf =>
        {
            SelectedMediaFile = mf;
            vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
            // Keep the gallery scrolled to the photo being viewed so that when the
            // viewer closes, the thumbnail is already in view on the main panel.
            ScrollIntoViewRequested?.Invoke(mf);
        };

        // Non-mutating commands — delegate directly.
        vm.OpenInExplorerCommand    = OpenInExplorerCommand;
        vm.CopyFullPathCommand      = CopyFullPathCommand;
        vm.CopyToFolderCommand      = CopyToFolderCommand;
        vm.ReanalyzeSelectedCommand = ReanalyzeSelectedCommand;
        vm.ToggleFavoriteCommand    = ToggleFavoriteCommand;
        vm.RemoveFromLibraryLabel   = RemoveFromLibraryLabel;
        vm.DeletePermanentlyLabel   = DeletePermanentlyLabel;
        vm.SendToMenuItems          = SendToMenuItems;

        // Mutating commands — wrap so the viewer's photo list refreshes and advances
        // past removed/excluded photos after each action.
        vm.ExcludeFolderCommand = new AsyncRelayCommand(async () =>
        {
            await ExcludeFolderAsync(); // already calls LoadAsync() internally
            vm.UpdatePhotoList(MediaFiles.ToList());
            vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
        });
        vm.IncludeFolderCommand = new AsyncRelayCommand(async () =>
        {
            await IncludeFolderAsync();
            vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
        });
        vm.ExcludeImageCommand = new AsyncRelayCommand(async () =>
        {
            await ExcludeImageAsync();
            vm.UpdatePhotoList(MediaFiles.ToList());
        });
        vm.RemoveFromLibraryCommand = new AsyncRelayCommand(async () =>
        {
            await RemoveFromLibraryAsync();
            vm.UpdatePhotoList(MediaFiles.ToList());
        });
        vm.DeletePermanentlyCommand = new AsyncRelayCommand(async () =>
        {
            await DeletePermanentlyAsync();
            vm.UpdatePhotoList(MediaFiles.ToList());
        });

        // Face box click — close viewer and filter gallery to that person.
        vm.RequestShowPerson += personId =>
        {
            window.Close();
            _ = FilterByPersonAsync(personId);
        };

        vm.Open(MediaFiles.ToList(), idx);
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // If the user excluded a folder that had already-imported photos,
        // those photos were removed from the DB inside SettingsViewModel.
        // Reload the exclusion cache and gallery so they disappear from the grid.
        var vm = window.DataContext as SettingsViewModel;
        if (vm?.LibraryModified == true)
        {
            await LoadExclusionCacheAsync();
            await LoadAsync();
            StatusText = "Excluded folder removed from library.";
        }

        // Rebuild Send To menu in case the user added or removed external editors.
        RebuildSendToMenuItems();
    }

    [RelayCommand]
    private async Task ScanDrivesAsync()
    {
        if (await CheckExpressCeilingAsync()) return;
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<ScanDrivesWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // If the user clicked Import in ScanDrives, enqueue the folders.
        // If an import is already running, the folders are appended to the on-disk queue
        // and picked up automatically — the caller does not double-await the worker.
        var scanVm = (ScanDrivesViewModel)window.DataContext;
        var folders = scanVm.SelectedFolderPaths;
        if (folders.Count > 0)
        {
            if (!Progress.IsRunning)
            {
                AttachTaskbar(Progress);
                AttachPhotoCallback(Progress);
                AttachExpressLimitCallback(Progress);
            }
            await Progress.RunMultiFolderAsync(folders);
            if (!Progress.IsRunning)
            {
                ClearTaskbar();
                StatusText = Progress.ResultMessage;
            }
            _ = _folderWatcher.RefreshFoldersAsync();
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<FindDuplicatesWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
        await LoadAsync();
    }

    // ── Favorite toggle ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedMediaFile == null) return;

        // Capture before any collection change can overwrite SelectedMediaFile.
        var target = SelectedMediaFile;
        target.IsFavorite = !target.IsFavorite;
        await WithRepo(r => r.UpdateAsync(target));
        OnPropertyChanged(nameof(SelectedIsFavorite));

        // Refresh the item's bindings in-place using the Replace notification.
        // Using the indexer keeps the existing container alive (thumbnail stays loaded).
        // Remove+Insert would destroy the container, blank the thumbnail, and — more
        // critically — trigger SelectionChanged, which changes SelectedMediaFile to the
        // adjacent item before the Insert can put the original back.
        var idx = MediaFiles.IndexOf(target);
        if (idx >= 0)
        {
            MediaFiles[idx] = target;   // Replace notification; container is NOT recreated
            SelectedMediaFile = target; // Restore selection (Replace may deselect the item)
        }

        // Remove from view if browsing Favorites and just un-favorited.
        if (ActiveView == GalleryView.Favorites && !target.IsFavorite)
            await LoadAsync();
    }

    partial void OnIsEditingDescriptionChanged(bool value)
    {
        RefreshDescriptionProperties();
    }

    partial void OnIsEditingCaptionChanged(bool value)
    {
        RefreshCaptionProperties();
    }

    // ── Selection ────────────────────────────────────────────────────────────

    partial void OnSelectedMediaFileChanged(MediaFile? value)
    {
        _selectionAnchor = value;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnySelected));
        OnPropertyChanged(nameof(SelectedIsOffline));
        OnPropertyChanged(nameof(IsReanalyzeSelectedEnabled));
        OnPropertyChanged(nameof(SelectedFolderIsExcluded));
        OnPropertyChanged(nameof(HasAiDescription));
        OnPropertyChanged(nameof(DescriptionAttemptedButFailed));
        IsEditingDescription = false;
        RefreshDescriptionProperties();  // always call directly — setter has equality guard
        IsEditingCaption = false;
        RefreshCaptionProperties();
        OnPropertyChanged(nameof(SelectedDateText));
        OnPropertyChanged(nameof(SelectedTimeText));
        OnPropertyChanged(nameof(SelectedDimensionsText));
        OnPropertyChanged(nameof(SelectedFileSizeText));
        OnPropertyChanged(nameof(SelectedIsFavorite));
        OnPropertyChanged(nameof(SelectedApertureText));
        OnPropertyChanged(nameof(SelectedShutterText));
        OnPropertyChanged(nameof(SelectedIsoText));
        OnPropertyChanged(nameof(SelectedFocalLengthText));
        OnPropertyChanged(nameof(HasAperture));
        OnPropertyChanged(nameof(HasShutterSpeed));
        OnPropertyChanged(nameof(HasIso));
        OnPropertyChanged(nameof(HasFocalLength));
        OnPropertyChanged(nameof(HasExifData));
        OnPropertyChanged(nameof(HasGps));
        OnPropertyChanged(nameof(SelectedGpsText));
        SelectedTags.Clear();
        OnPropertyChanged(nameof(HasSelectedTags));
        AllMetadata.Clear();
        _allMetadataLoaded = false;
        if (value != null)
        {
            _ = LoadTagsAsync(value.Id);
            // Bump to front of vision queue so the viewed photo gets its description first.
            if (value.AnalysisStatus == AnalysisStatus.VisionPending)
                PrioritizeVision(value.Id);
        }
    }

    private async Task LoadTagsAsync(Guid id)
    {
        var full = await WithRepo(r => r.GetByIdAsync(id));
        SelectedTags.Clear();
        if (full?.Tags != null)
            foreach (var tag in full.Tags
                .OrderByDescending(t => t.IsAIGenerated)
                .ThenByDescending(t => t.Confidence))
                SelectedTags.Add(new TagViewModel(tag.Id, tag.Name, tag.IsAIGenerated));
        OnPropertyChanged(nameof(HasSelectedTags));
    }

    [RelayCommand]
    private async Task RemoveTagAsync(TagViewModel tag)
    {
        if (SelectedMediaFile == null) return;
        await WithRepo(r => r.RemoveTagFromPhotoAsync(SelectedMediaFile.Id, tag.Id));
        SelectedTags.Remove(tag);
        OnPropertyChanged(nameof(HasSelectedTags));
    }

    [RelayCommand]
    private async Task LoadAllMetadataAsync()
    {
        if (SelectedMediaFile == null || _allMetadataLoaded) return;
        var path = SelectedMediaFile.FilePath;
        IReadOnlyList<MetadataTag> tags;
        using (var scope = _scopeFactory.CreateScope())
        {
            var import = scope.ServiceProvider.GetRequiredService<IImportService>();
            tags = await import.ReadAllTagsAsync(path);
        }
        AllMetadata.Clear();
        foreach (var t in tags) AllMetadata.Add(t);
        _allMetadataLoaded = true;
    }

    // ── Batch tag popup ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _isAddTagPopupOpen;
    [ObservableProperty] private string _batchTagText = "";
    public ObservableCollection<string> TagSuggestions { get; } = [];
    private List<string> _allTagNames = [];

    public string AddTagPopupTitle => HasMultiSelection
        ? $"Add tag to {SelectedCount} photos"
        : "Add tag to photo";

    [RelayCommand]
    private async Task ShowAddTagPopup()
    {
        if (!HasAnySelected) return;
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        _allTagNames = await repo.GetAllTagNamesAsync();
        BatchTagText = "";
        TagSuggestions.Clear();
        OnPropertyChanged(nameof(AddTagPopupTitle));
        IsAddTagPopupOpen = true;
    }

    partial void OnBatchTagTextChanged(string value)
    {
        TagSuggestions.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var name in _allTagNames
            .Where(n => n.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Take(6))
            TagSuggestions.Add(name);
    }

    [RelayCommand]
    private async Task ConfirmBatchTag()
    {
        var name = BatchTagText.Trim();
        IsAddTagPopupOpen = false;
        if (string.IsNullOrEmpty(name)) return;

        var targets = HasMultiSelection
            ? MultiSelectedItems.ToList()
            : SelectedMediaFile != null ? [SelectedMediaFile] : [];
        if (targets.Count == 0) return;

        int added = 0;
        Guid? newTagId = null;
        bool selectedPhotoTagged = false;

        foreach (var photo in targets)
        {
            // Each photo gets its own scope — EF Core can't share a DbContext across async gaps.
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
            var mf = await repo.GetByIdAsync(photo.Id);
            if (mf == null) continue;
            if (mf.Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue; // already has this tag — de-dup
            var tag = await repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(), TagCategory.Custom, false, 0f);
            newTagId = tag.Id;
            mf.Tags.Add(tag);
            await repo.UpdateAsync(mf);
            added++;
            if (SelectedMediaFile?.Id == photo.Id) selectedPhotoTagged = true;
        }

        // Update the detail panel tag list for the currently visible photo.
        if (selectedPhotoTagged && newTagId.HasValue)
        {
            SelectedTags.Add(new TagViewModel(newTagId.Value, name, false));
            OnPropertyChanged(nameof(HasSelectedTags));
        }

        // Cache for future autocomplete within this session.
        if (added > 0 && !_allTagNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            _allTagNames.Add(name);

        StatusText = added > 0
            ? $"Tag \"{name}\" added to {added} photo{(added == 1 ? "" : "s")}"
            : $"Tag \"{name}\" already present on all selected photos";
    }

    [RelayCommand]
    private void CloseAddTagPopup() => IsAddTagPopupOpen = false;

    [RelayCommand]
    private void SelectTagSuggestion(string tag)
    {
        BatchTagText = tag;
        TagSuggestions.Clear();
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (SelectedMediaFile == null || string.IsNullOrWhiteSpace(NewTagText)) return;
        var name = NewTagText.Trim();
        if (SelectedTags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            NewTagText = "";
            return;
        }
        // All three steps must share one DbContext so EF's change-tracker can link the
        // Tag entity returned by GetOrCreateTagAsync to the MediaFile loaded above it.
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var mf = await repo.GetByIdAsync(SelectedMediaFile.Id);
        if (mf == null) return;
        var tag = await repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(), TagCategory.Custom, false, 0f);
        if (!mf.Tags.Any(t => t.Id == tag.Id))
        {
            mf.Tags.Add(tag);
            await repo.UpdateAsync(mf);
        }
        SelectedTags.Add(new TagViewModel(tag.Id, name, false));
        OnPropertyChanged(nameof(HasSelectedTags));
        NewTagText = "";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Called by WM_DEVICECHANGE hook when a drive is connected or disconnected.
    // Re-checks drive accessibility for all items currently in the grid.
    // IsOffline raises PropertyChanged, so grayscale/overlay bindings update automatically.
    public async Task RefreshOfflineStatusAsync()
    {
        var snapshot = MediaFiles.ToList();
        var count = await Task.Run(() => MarkOfflineFiles(snapshot));
        OfflineCount = count;
        OnPropertyChanged(nameof(HasOfflineFiles));
        OnPropertyChanged(nameof(OfflineStatusText));
        OnPropertyChanged(nameof(SelectedIsOffline));
    }

    private static int MarkOfflineFiles(IEnumerable<MediaFile> files)
    {
        var driveCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int offline = 0;
        foreach (var mf in files)
        {
            var root = Path.GetPathRoot(mf.FilePath);
            if (string.IsNullOrEmpty(root)) continue;
            if (!driveCache.TryGetValue(root, out var accessible))
            {
                accessible = Directory.Exists(root);
                driveCache[root] = accessible;
            }
            mf.IsOffline = !accessible;
            if (mf.IsOffline) offline++;
        }
        return offline;
    }

    /// <summary>
    /// Returns true if <paramref name="mf"/> would appear under the current view/filter.
    /// Used by live-import and vision callbacks to decide whether to add a photo to the gallery
    /// without a full DB reload. Uses simple case-insensitive substring matching — a good
    /// approximation of FTS5 for the live-update case. Semantic search results are not
    /// evaluated in-memory (too expensive); photos are skipped and the user can re-run search.
    /// </summary>
    private bool MatchesCurrentView(MediaFile mf)
    {
        // View filters — new photos are never favorites or album members.
        if (ActiveView == GalleryView.Favorites) return false;
        if (ActiveView == GalleryView.Album)     return false;

        // No search active → show everything.
        if (!IsSearchActive) return true;

        // Semantic search cannot be evaluated in-memory — skip live adds.
        if (IsSemanticSearch) return false;

        // Simple text match across FTS-indexed fields.
        var q = SearchQuery.Trim();
        bool Has(string? s) => !string.IsNullOrEmpty(s) &&
                               s.Contains(q, StringComparison.OrdinalIgnoreCase);

        return Has(mf.FileName)
            || Has(mf.AiDescription)
            || Has(mf.UserDescription)
            || Has(mf.UserCaption)
            || Has(mf.CameraMake)
            || Has(mf.CameraModel)
            || (mf.Tags?.Any(t => Has(t.Name)) ?? false);
    }

}
