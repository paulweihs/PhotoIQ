using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using static PhotoWell.Common.FormatUtilities;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// ViewModel for the photo detail viewer window (double-click to open full-screen viewer).
/// </summary>
/// <remarks>
/// Manages photo navigation (next/previous), image loading/caching, tag display, face detection visualization,
/// and delegated commands (favorite, exclude, delete, etc.) that invoke MainViewModel handlers.
///
/// Key features:
/// - Image loading with decode semaphore to prevent concurrent load storms
/// - CancellationToken management for cancelling in-flight image loads
/// - Face rectangle overlay with click-to-person-jump navigation
/// - Panning and zooming (handled in code-behind PhotoViewerWindow.xaml.cs)
/// - EXIF metadata extraction and formatting
/// - Keyboard shortcuts (arrow keys, Escape to close)
/// - Context menu integration for file operations (copy, explorer, exclude, delete, etc.)
/// </remarks>
public partial class PhotoViewerViewModel : ObservableObject
{
    private readonly IMediaFileRepository   _repo;
    private readonly IPersonRepository      _personRepo;
    private readonly IFaceRecognitionService _faceRecognitionService;
    private IReadOnlyList<MediaFile> _photos = [];
    private int _index;
    private CancellationTokenSource _imageCts      = new();
    private readonly SemaphoreSlim  _decodeSemaphore = new(1, 1);

    [ObservableProperty] private MediaFile? _current;
    [ObservableProperty] private object?    _currentImage;
    [ObservableProperty] private bool       _isImageLoading;
    [ObservableProperty] private ObservableCollection<TagViewModel> _tags  = [];
    [ObservableProperty] private string _newTagText = "";
    [ObservableProperty] private ObservableCollection<FaceItemViewModel> _faces = [];
    [ObservableProperty] private bool _showFacePrompt;

    public event Action?       RequestClose;
    public event Action?       RequestRedrawFaceOverlay;
    public event Action<Guid>? RequestShowPerson;   // raised when user clicks a named face box
    public event Action<(Guid PersonId, string PersonName)>? RequestFaceReview;   // raised after inline face assignment to show bulk review dialog

    /// <summary>Gets a value indicating whether the current photo has detected faces.</summary>
    public bool HasFaces => Faces.Count > 0;

    /// <summary>Navigates to the person detail view if the clicked face has an identified person.</summary>
    [RelayCommand]
    private void JumpToPerson(FaceItemViewModel face)
    {
        if (face.PersonId.HasValue)
            RequestShowPerson?.Invoke(face.PersonId.Value);
    }

    // ── Delegated commands — set by MainViewModel before ShowDialog() ─────────
    /// <summary>
    /// Invoked when the viewer navigates to a new photo, so MainViewModel can sync its single-selection state.
    /// </summary>
    /// <remarks>Set by MainViewModel before the viewer window opens.</remarks>
    public Action<MediaFile>? PhotoChanged     { get; set; }
    /// <summary>
    /// Invoked by MainViewModel after a re-analysis completes so the viewer can refresh
    /// its description and metadata panel without closing and reopening.
    /// </summary>
    public Action<MediaFile>? PhotoReanalyzed { get; set; }

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
    public ICommand? ExportAsBundleCommand         { get; set; }
    public ICommand? RotateClockwiseCommand        { get; set; }
    public ICommand? RotateCounterClockwiseCommand { get; set; }
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
    public bool   HasGps          => Current?.Latitude != null && Current?.Longitude != null;
    public string GpsText
    {
        get
        {
            if (Current is not { Latitude: double lat, Longitude: double lon }) return "";
            return $"{Math.Abs(lat):F4}° {(lat >= 0 ? "N" : "S")},  {Math.Abs(lon):F4}° {(lon >= 0 ? "E" : "W")}";
        }
    }

    [RelayCommand]
    private void OpenInMaps()
    {
        if (Current is not { Latitude: double lat, Longitude: double lon }) return;
        var uri = $"https://maps.google.com/maps?q={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* ignore if no browser available */ }
    }

    /// <summary>Initializes a new instance of the PhotoViewerViewModel.</summary>
    /// <param name="repo">Repository for loading full photo details and managing metadata.</param>
    /// <param name="personRepo">Repository for resolving face person identities.</param>
    /// <param name="faceRecognitionService">Service for invalidating face embedding cache after manual assignments.</param>
    public PhotoViewerViewModel(IMediaFileRepository repo, IPersonRepository personRepo, IFaceRecognitionService faceRecognitionService)
    {
        _repo                     = repo;
        _personRepo               = personRepo;
        _faceRecognitionService   = faceRecognitionService;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Opens the viewer with a photo list and starts at the specified index.
    /// </summary>
    /// <remarks>
    /// Called by MainViewModel.ShowPhotoViewer before the viewer window opens.
    /// Triggers an async load of the first photo's details and image.
    /// </remarks>
    /// <param name="photos">The list of MediaFiles to browse through.</param>
    /// <param name="startIndex">The index of the initial photo to display.</param>
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

    /// <summary>Accept the AI suggestion for a single face — links and confirms it.</summary>
    [RelayCommand]
    private async Task AcceptSuggestion(FaceItemViewModel face)
    {
        if (string.IsNullOrEmpty(face.SuggestedName)) return;
        await AssignFacePersonAsync(face, face.SuggestedName);
        face.SuggestedName = null;
        RequestRedrawFaceOverlay?.Invoke();
    }

    /// <summary>Dismiss the AI suggestion for a single face without assigning anyone.</summary>
    [RelayCommand]
    private void RejectSuggestion(FaceItemViewModel face)
    {
        face.SuggestedName = null;
    }

    /// <summary>
    /// Confirms every face that already has a matched name, and accepts the AI suggestion
    /// for unnamed faces that have one. Marks the photo as reviewed.
    /// </summary>
    [RelayCommand]
    private async Task AcceptAllConfirmed()
    {
        if (Current == null) return;
        ShowFacePrompt = false;

        foreach (var fi in Faces.ToList())
        {
            if (!string.IsNullOrEmpty(fi.NameInput))
            {
                // User typed something — treat as manual entry
                var name = fi.NameInput.Trim();
                if (string.Equals(name, fi.MatchedName, StringComparison.OrdinalIgnoreCase))
                    await _repo.ConfirmFaceAsync(fi.FaceId);
                else
                    await AssignFacePersonAsync(fi, name);
            }
            else if (!string.IsNullOrEmpty(fi.MatchedName))
            {
                // Already matched — just confirm
                await _repo.ConfirmFaceAsync(fi.FaceId);
            }
            else if (!string.IsNullOrEmpty(fi.SuggestedName))
            {
                // Accept the AI suggestion
                await AssignFacePersonAsync(fi, fi.SuggestedName);
                fi.SuggestedName = null;
            }
        }

        Current.FacesReviewed = true;
        await _repo.MarkFacesReviewedAsync(Current.Id);
        await _repo.RefreshFtsForPhotoAsync(Current.Id);
        RequestRedrawFaceOverlay?.Invoke();
    }

    /// <summary>Mark this photo's faces as reviewed so the prompt never appears again.</summary>
    [RelayCommand]
    private async Task SkipFacePromptAlways()
    {
        if (Current == null) return;
        ShowFacePrompt = false;
        Current.FacesReviewed = true;
        await _repo.MarkFacesReviewedAsync(Current.Id);
    }

    /// <summary>Adds a user tag to the current photo and saves it to the database.</summary>
    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagText.Trim();
        if (string.IsNullOrWhiteSpace(name) || Current == null) return;

        var mf = await _repo.GetByIdAsync(Current.Id);
        if (mf == null) return;

        var tag = await _repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(), TagCategory.Custom, false, 0f);
        if (!mf.Tags.Any(t => t.Id == tag.Id))
        {
            mf.Tags.Add(tag);
            await _repo.UpdateAsync(mf);
            Tags.Add(new TagViewModel(tag.Id, tag.Name, false));
            OnPropertyChanged(nameof(HasTags));
        }
        NewTagText = "";
    }

    /// <summary>Removes a user tag from the current photo.</summary>
    [RelayCommand]
    private async Task RemoveTagAsync(TagViewModel tag)
    {
        if (Current == null) return;
        await _repo.RemoveTagFromPhotoAsync(Current.Id, tag.Id);
        Tags.Remove(tag);
        OnPropertyChanged(nameof(HasTags));
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

            // Same name as already matched — confirm the existing assignment but no re-link needed.
            if (string.Equals(name, fi.MatchedName, StringComparison.OrdinalIgnoreCase))
            {
                await _repo.ConfirmFaceAsync(fi.FaceId);
                continue;
            }

            var normalized = name.ToLowerInvariant();
            var person = await _personRepo.FindOrCreateByNameAsync(name, normalized);
            await _personRepo.LinkFaceToPersonAsync(fi.FaceId, person.Id, 1.0);
            await _repo.ConfirmFaceAsync(fi.FaceId);
            if (fi.Embedding != null)
                await _faceRecognitionService.TrackManualAssignmentAsync(person.Id, fi.Embedding);
        }

        Current.FacesReviewed = true;
        await _repo.MarkFacesReviewedAsync(Current.Id);
        await _repo.RefreshFtsForPhotoAsync(Current.Id);
        RequestRedrawFaceOverlay?.Invoke();
    }

    // ── Public helpers for face tagging UI ────────────────────────────────────

    /// <summary>Returns all named persons for the type-ahead dropdown in face assignment popup.</summary>
    public Task<IReadOnlyList<Person>> GetAllNamedPersonsAsync()
        => _personRepo.GetAllNamedAsync();

    /// <summary>Returns the AI's best guess for an unidentified face, or null if no named persons exist.</summary>
    public async Task<(string Name, float Confidence)?> GetAiSuggestionAsync(byte[]? embedding)
    {
        if (embedding == null || embedding.Length == 0) return null;
        var match = await _faceRecognitionService.GetTopNamedMatchAsync(embedding);
        if (match == null) return null;
        return (match.Value.Name, match.Value.Confidence);
    }

    /// <summary>Returns faces auto-linked to a person in unreviewed photos, ordered by confidence (highest first).</summary>
    public Task<IReadOnlyList<(Face Face, MediaFile Photo)>> GetUnconfirmedFacesForPersonAsync(Guid personId)
        => _repo.GetUnconfirmedFacesForPersonAsync(personId);

    /// <summary>Assigns a face to a person by name, triggering cache invalidation and review dialog.</summary>
    public async Task AssignFacePersonAsync(FaceItemViewModel face, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed    = name.Trim();
        var normalized = trimmed.ToLowerInvariant();
        var person = await _personRepo.FindOrCreateByNameAsync(trimmed, normalized);
        await _personRepo.LinkFaceToPersonAsync(face.FaceId, person.Id, 1.0);
        await _repo.ConfirmFaceAsync(face.FaceId);
        face.UpdateAssignment(person.Id, trimmed);
        if (face.Embedding != null)
            await _faceRecognitionService.TrackManualAssignmentAsync(person.Id, face.Embedding);
        else
            await _faceRecognitionService.InvalidateCacheAsync();
        if (Current != null)
            await _repo.RefreshFtsForPhotoAsync(Current.Id);
        RequestFaceReview?.Invoke((person.Id, trimmed));
    }

    /// <summary>
    /// Creates a manual person mention for the current photo — no bounding box or embedding.
    /// Survives re-detection. Used when the user tags someone whose face wasn't auto-detected.
    /// </summary>
    public async Task AddPersonMentionAsync(string name)
    {
        if (Current == null || string.IsNullOrWhiteSpace(name)) return;
        var trimmed    = name.Trim();
        var normalized = trimmed.ToLowerInvariant();
        var person = await _personRepo.FindOrCreateByNameAsync(trimmed, normalized);

        var face = new Face
        {
            MediaFileId     = Current.Id,
            PersonId        = person.Id,
            IsUserConfirmed = true,
            IsManualMention = true,
            DateDetected    = DateTime.UtcNow,
        };
        await _repo.AddFaceAsync(face);
        face.Person = person;

        Faces.Add(new FaceItemViewModel(face, person.Name));
        OnPropertyChanged(nameof(HasFaces));

        if (face.Embedding != null)
            await _faceRecognitionService.TrackManualAssignmentAsync(person.Id, face.Embedding);
        await _repo.RefreshFtsForPhotoAsync(Current.Id);
    }

    /// <summary>Unlinks a face from its auto-assigned person (used in review dialog "No" button and popup unlink).</summary>
    public async Task UnlinkFaceAsync(Guid faceId)
    {
        await _repo.UnlinkFaceFromPersonAsync(faceId);
        await _faceRecognitionService.InvalidateCacheAsync();
    }

    /// <summary>Marks a face as user-confirmed so it survives re-detection (used in review dialog "Yes" button).</summary>
    public Task ConfirmFaceAsync(Guid faceId)
        => _repo.ConfirmFaceAsync(faceId);

    /// <summary>Suppresses the review dialog for a person permanently ("Never ask about [name]").</summary>
    public Task SuppressPersonPromptAsync(Guid personId)
        => _personRepo.SuppressPromptAsync(personId);

    /// <summary>Marks a photo's faces as reviewed so the initial prompt doesn't appear again.</summary>
    public Task MarkFacesReviewedAsync(Guid photoId)
        => _repo.MarkFacesReviewedAsync(photoId);

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>Re-decodes the current image from disk (e.g. after rotation). Does not reload tags or faces.</summary>
    public Task ReloadCurrentImageAsync() => LoadImageAsync();

    /// <summary>
    /// Updates the viewer with a freshly re-analyzed <see cref="MediaFile"/>.
    /// Called via the <see cref="PhotoReanalyzed"/> callback after a re-analysis completes.
    /// Replaces the photo in the internal list, sets Current, and notifies all derived properties
    /// so the description and metadata panel refresh without requiring navigation.
    /// </summary>
    public void ApplyReanalyzedPhoto(MediaFile updated)
    {
        // Replace the stale reference in the nav list so forward/back navigation picks up the new data.
        // _photos is typed as IReadOnlyList but the concrete type (ObservableCollection / List) is
        // mutable via IList<T>, so cast to that for index-based replacement.
        if (_photos is IList<MediaFile> mutable)
        {
            for (int i = 0; i < mutable.Count; i++)
            {
                if (mutable[i].Id == updated.Id)
                {
                    mutable[i] = updated;
                    break;
                }
            }
        }

        if (Current?.Id == updated.Id)
        {
            Current = updated;
            NotifyDerived();
        }
    }

    private async Task LoadCurrentAsync()
    {
        Current = _photos[_index];
        PhotoChanged?.Invoke(Current);
        NotifyDerived();

        // Reset face prompt state on each navigation.
        ShowFacePrompt = false;
        Faces.Clear();
        OnPropertyChanged(nameof(HasFaces));

        await LoadImageAsync();
        var ct = _imageCts.Token;

        // Tags are not eagerly loaded in the gallery list — fetch from DB.
        if (!ct.IsCancellationRequested)
        {
            var full = await _repo.GetByIdAsync(Current.Id);
            if (!ct.IsCancellationRequested)
            {
                Tags.Clear();
                if (full?.Tags != null)
                    foreach (var t in full.Tags.OrderByDescending(t => t.IsAIGenerated).ThenByDescending(t => t.Confidence))
                        Tags.Add(new TagViewModel(t.Id, t.Name, t.IsAIGenerated));
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

                // Async: load AI suggestions for unnamed faces so they appear as ghost text.
                if (ShowFacePrompt)
                    _ = LoadSuggestionsAsync(ct);
            }
        }
    }

    private async Task LoadSuggestionsAsync(CancellationToken ct)
    {
        foreach (var face in Faces.Where(f => !f.IsNamed && string.IsNullOrEmpty(f.NameInput) && f.Embedding != null).ToList())
        {
            if (ct.IsCancellationRequested) return;
            var suggestion = await GetAiSuggestionAsync(face.Embedding);
            if (suggestion != null && !ct.IsCancellationRequested)
            {
                face.SuggestedName  = suggestion.Value.Name;
                face.SuggestionPct  = (int)(suggestion.Value.Confidence * 100);
            }
        }
    }

    private async Task LoadImageAsync()
    {
        if (Current is null) return;

        // Cancel and dispose any in-flight decode from a previous navigation.
        _imageCts.Cancel();
        _imageCts.Dispose();
        _imageCts = new CancellationTokenSource();
        var ct          = _imageCts.Token;
        var path        = Current.FilePath;
        var userRotation = Current.UserRotation;   // capture before await

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
                    // Combine EXIF rotation with any user-applied rotation override.
                    var exifDeg  = RotationToDegrees(ReadExifRotation(path));
                    b.Rotation   = DegreesToRotation(exifDeg + userRotation);
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
        OnPropertyChanged(nameof(HasGps));
        OnPropertyChanged(nameof(GpsText));
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

    /// <summary>
    /// Reads the EXIF Orientation tag from a JPEG (or any WIC-supported format) using the
    /// lightweight WPF BitmapDecoder — no full pixel decode, just metadata.
    /// Maps EXIF values to WPF Rotation so BitmapImage.Rotation corrects the display without
    /// a second decode pass or a TransformedBitmap wrapper.
    ///
    /// EXIF orientation values:
    ///   1 = normal, 3 = 180°, 6 = 90° CW (camera held rotated left), 8 = 270° CW (rotated right).
    ///   Flip variants (2/4/5/7) are not correctable via BitmapImage.Rotation alone; they are
    ///   extremely rare in practice (no consumer camera produces them) and are left as-is.
    /// </summary>
    private static int RotationToDegrees(Rotation r) => r switch
    {
        Rotation.Rotate90  => 90,
        Rotation.Rotate180 => 180,
        Rotation.Rotate270 => 270,
        _                  => 0
    };

    private static Rotation DegreesToRotation(int degrees) => ((degrees % 360 + 360) % 360) switch
    {
        90  => Rotation.Rotate90,
        180 => Rotation.Rotate180,
        270 => Rotation.Rotate270,
        _   => Rotation.Rotate0
    };

    private static Rotation ReadExifRotation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);           // None = don't keep pixels in memory

            if (decoder.Frames.Count == 0) return Rotation.Rotate0;
            if (decoder.Frames[0].Metadata is not BitmapMetadata meta) return Rotation.Rotate0;

            // "System.Photo.Orientation" is the Windows Shell property that exposes the EXIF tag.
            var raw = meta.GetQuery("System.Photo.Orientation");
            ushort orientation = raw is ushort u ? u : (raw is uint ui ? (ushort)ui : (ushort)1);

            return orientation switch
            {
                3 => Rotation.Rotate180,
                6 => Rotation.Rotate90,
                8 => Rotation.Rotate270,
                _ => Rotation.Rotate0,
            };
        }
        catch
        {
            return Rotation.Rotate0;
        }
    }

}
