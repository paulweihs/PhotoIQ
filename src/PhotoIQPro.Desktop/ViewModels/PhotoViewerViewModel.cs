using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using static PhotoIQPro.Common.FormatUtilities;

namespace PhotoIQPro.Desktop.ViewModels;

public partial class PhotoViewerViewModel : ObservableObject
{
    private readonly IMediaFileRepository _repo;
    private readonly IPersonRepository    _personRepo;
    private IReadOnlyList<MediaFile> _photos = [];
    private int _index;
    private CancellationTokenSource _imageCts      = new();
    private readonly SemaphoreSlim  _decodeSemaphore = new(1, 1);

    [ObservableProperty] private MediaFile? _current;
    [ObservableProperty] private object?    _currentImage;
    [ObservableProperty] private bool       _isImageLoading;
    [ObservableProperty] private ObservableCollection<string> _tags  = [];
    [ObservableProperty] private ObservableCollection<FaceItemViewModel> _faces = [];
    [ObservableProperty] private bool _showFacePrompt;

    public event Action?       RequestClose;
    public event Action<Guid>? RequestShowPerson;   // raised when user clicks a named face box

    public bool HasFaces => Faces.Count > 0;

    [RelayCommand]
    private void JumpToPerson(FaceItemViewModel face)
    {
        if (face.PersonId.HasValue)
            RequestShowPerson?.Invoke(face.PersonId.Value);
    }

    // ── Delegated commands — set by MainViewModel before ShowDialog() ─────────
    /// <summary>Invoked when the viewer navigates to a new photo, so MainViewModel can sync its selection.</summary>
    public Action<MediaFile>? PhotoChanged { get; set; }

    public ICommand? OpenInExplorerCommand      { get; set; }
    public ICommand? CopyFullPathCommand        { get; set; }
    public ICommand? CopyToFolderCommand        { get; set; }
    public ICommand? ReanalyzeSelectedCommand   { get; set; }
    public ICommand? ToggleFavoriteCommand      { get; set; }
    public ICommand? ExcludeFolderCommand       { get; set; }
    public ICommand? IncludeFolderCommand       { get; set; }
    public ICommand? ExcludeImageCommand        { get; set; }
    public ICommand? RemoveFromLibraryCommand   { get; set; }
    public ICommand? DeletePermanentlyCommand   { get; set; }
    public string    RemoveFromLibraryLabel     { get; set; } = "Remove from Library";
    public string    DeletePermanentlyLabel     { get; set; } = "Delete Permanently";
    public System.Collections.IEnumerable? SendToMenuItems { get; set; }

    [ObservableProperty] private bool _isCurrentFolderExcluded;

    // ── Nav state ────────────────────────────────────────────────────────────
    public bool HasPrev => _index > 0;
    public bool HasNext => _index < _photos.Count - 1;
    public string Counter => _photos.Count > 0 ? $"{_index + 1}  /  {_photos.Count}" : "";

    // ── Display strings ──────────────────────────────────────────────────────
    public bool   HasAiDescription             => !string.IsNullOrEmpty(Current?.AiDescription);
    public bool   DescriptionAttemptedButFailed => Current?.AiDescription == string.Empty;
    public bool   HasTags                      => Tags.Count > 0;
    public bool   HasExif                      => !string.IsNullOrEmpty(ExifLine);
    public string AiModelText                  => !string.IsNullOrEmpty(Current?.AiModelUsed)
                                                  ? $"via {Current!.AiModelUsed}"
                                                  : "";
    public bool   HasAiModelText               => !string.IsNullOrEmpty(Current?.AiModelUsed);

    public string DateText        => Current?.DateTaken?.ToString("MMMM d, yyyy  ·  h:mm tt") ?? "";
    public string DimensionsText  => Current is { Width: > 0, Height: > 0 } m ? $"{m.Width} × {m.Height}" : "";
    public string FileSizeText    => Current != null ? FormatFileSize(Current.FileSize) : "";
    public string CameraText      => BuildCameraText();
    public string ExifLine        => BuildExifLine();

    public PhotoViewerViewModel(IMediaFileRepository repo, IPersonRepository personRepo)
    {
        _repo       = repo;
        _personRepo = personRepo;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public void Open(IReadOnlyList<MediaFile> photos, int startIndex)
    {
        _photos = photos;
        _index  = startIndex;
        _ = LoadCurrentAsync();
    }

    /// <summary>
    /// Replaces the photo list (called after an exclude/remove/delete from the context menu)
    /// and navigates to the nearest remaining photo. Closes the viewer if the list is empty.
    /// </summary>
    public void UpdatePhotoList(IReadOnlyList<MediaFile> newPhotos)
    {
        var currentId = Current?.Id;
        _photos = newPhotos;

        if (_photos.Count == 0) { RequestClose?.Invoke(); return; }

        // Stay on the current photo if it still exists; otherwise advance to same index.
        var sameIdx = -1;
        for (int i = 0; i < _photos.Count; i++)
            if (_photos[i].Id == currentId) { sameIdx = i; break; }

        _index = sameIdx >= 0
            ? sameIdx
            : Math.Clamp(_index, 0, _photos.Count - 1);

        _ = LoadCurrentAsync();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasPrev))]
    private void Prev() { _index--; _ = LoadCurrentAsync(); }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next() { _index++; _ = LoadCurrentAsync(); }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    /// <summary>Dismiss the face prompt for this navigation only.</summary>
    [RelayCommand]
    private void SkipFacePromptOnce() => ShowFacePrompt = false;

    /// <summary>Mark this photo's faces as reviewed so the prompt never appears again.</summary>
    [RelayCommand]
    private async Task SkipFacePromptAlways()
    {
        if (Current == null) return;
        ShowFacePrompt = false;
        Current.FacesReviewed = true;
        await _repo.MarkFacesReviewedAsync(Current.Id);
    }

    /// <summary>
    /// Saves any typed names: links each face to an existing or new Person,
    /// then marks the photo's faces as reviewed.
    /// </summary>
    [RelayCommand]
    private async Task SaveFaceNames()
    {
        if (Current == null) return;
        ShowFacePrompt = false;

        foreach (var fi in Faces)
        {
            var name = fi.NameInput.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Same name as already matched — no change needed.
            if (string.Equals(name, fi.MatchedName, StringComparison.OrdinalIgnoreCase)) continue;

            var normalized = name.ToLowerInvariant();
            var person = await _personRepo.FindOrCreateByNameAsync(name, normalized);
            await _personRepo.LinkFaceToPersonAsync(fi.FaceId, person.Id, 1.0);
        }

        Current.FacesReviewed = true;
        await _repo.MarkFacesReviewedAsync(Current.Id);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task LoadCurrentAsync()
    {
        Current = _photos[_index];
        PhotoChanged?.Invoke(Current);
        NotifyDerived();

        // Reset face prompt state on each navigation.
        ShowFacePrompt = false;
        Faces.Clear();
        OnPropertyChanged(nameof(HasFaces));

        // Cancel and dispose any in-flight decode from a previous navigation.
        _imageCts.Cancel();
        _imageCts.Dispose();
        _imageCts = new CancellationTokenSource();
        var ct   = _imageCts.Token;
        var path = Current.FilePath;

        // Clear previous image immediately so the viewer doesn't show stale content.
        CurrentImage   = null;
        IsImageLoading = true;

        // Wait for the exclusive decode slot. If navigation moves on while we're
        // queued here, WaitAsync throws OCE and we bail out without touching disk.
        // This means only the most-recently-requested image ever actually decodes.
        try { await _decodeSemaphore.WaitAsync(ct); }
        catch (OperationCanceledException) { IsImageLoading = false; return; }

        object? bmp;
        try
        {
            // Decode full-res on a background thread — EndInit() can take seconds for
            // large photos and must never run on the UI thread.
            bmp = await Task.Run(() =>
            {
                if (!File.Exists(path)) return null;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource        = new Uri(path);
                    b.DecodePixelWidth = 1600;
                    b.CacheOption      = BitmapCacheOption.OnLoad;
                    b.CreateOptions    = BitmapCreateOptions.IgnoreImageCache;
                    b.EndInit();
                    b.Freeze();
                    return (object?)b;
                }
                catch { return null; }
            });
        }
        finally { _decodeSemaphore.Release(); }

        // Guard: navigation may have moved on while we were decoding.
        if (!ct.IsCancellationRequested)
        {
            CurrentImage   = bmp;
            IsImageLoading = false;
        }

        // Tags are not eagerly loaded in the gallery list — fetch from DB.
        if (!ct.IsCancellationRequested)
        {
            var full = await _repo.GetByIdAsync(Current.Id);
            if (!ct.IsCancellationRequested)
            {
                Tags.Clear();
                if (full?.Tags != null)
                    foreach (var t in full.Tags.Where(t => t.IsAIGenerated).OrderByDescending(t => t.Confidence))
                        Tags.Add(t.Name);
                OnPropertyChanged(nameof(HasTags));
            }
        }

        // Load detected faces (only for photos that completed face detection).
        if (!ct.IsCancellationRequested && Current.FaceDetectionStatus == FaceDetectionStatus.Complete)
        {
            var photoId = Current.Id;
            var faceList = await _repo.GetFacesForPhotoAsync(photoId);

            if (!ct.IsCancellationRequested)
            {
                Faces.Clear();
                foreach (var f in faceList)
                    Faces.Add(new FaceItemViewModel(f, f.Person?.Name));

                OnPropertyChanged(nameof(HasFaces));

                // Show the prompt only on first view (FacesReviewed == false) and
                // only if there is at least one face.
                ShowFacePrompt = Faces.Count > 0 && !Current.FacesReviewed;
            }
        }
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(Counter));
        OnPropertyChanged(nameof(HasAiDescription));
        OnPropertyChanged(nameof(DescriptionAttemptedButFailed));
        OnPropertyChanged(nameof(AiModelText));
        OnPropertyChanged(nameof(HasAiModelText));
        OnPropertyChanged(nameof(HasExif));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(CameraText));
        OnPropertyChanged(nameof(ExifLine));
        OnPropertyChanged(nameof(HasFaces));
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private string BuildCameraText()
    {
        if (Current == null) return "";
        var parts = new[] { Current.CameraMake, Current.CameraModel }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", parts);
    }

    private string BuildExifLine()
    {
        if (Current == null) return "";
        var parts = new List<string>();
        if (Current.Aperture.HasValue)                       parts.Add($"f/{Current.Aperture:F1}");
        if (!string.IsNullOrEmpty(Current.ShutterSpeed))     parts.Add(Current.ShutterSpeed);
        if (Current.ISO.HasValue)                            parts.Add($"ISO {Current.ISO}");
        if (Current.FocalLength.HasValue)                    parts.Add($"{Current.FocalLength:F0}mm");
        return string.Join("  ·  ", parts);
    }

}
