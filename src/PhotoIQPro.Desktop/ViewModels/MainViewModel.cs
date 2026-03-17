using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Desktop.Views;

namespace PhotoIQPro.Desktop.ViewModels;

public enum GalleryView { AllPhotos, Favorites }

public partial class MainViewModel : ObservableObject
{
    private readonly IMediaFileRepository _repo;
    private readonly IImportService _import;

    [ObservableProperty] private ObservableCollection<MediaFile> _mediaFiles = [];
    [ObservableProperty] private MediaFile? _selectedMediaFile;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _photoCount;
    [ObservableProperty] private ObservableCollection<string> _selectedTags = [];
    [ObservableProperty] private GalleryView _activeView = GalleryView.AllPhotos;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private double _thumbnailSize = 180;
    [ObservableProperty] private bool _isLoading;

    public bool IsEmpty => PhotoCount == 0;
    public bool HasSelection => SelectedMediaFile != null;
    public bool HasAiDescription => !string.IsNullOrEmpty(SelectedMediaFile?.AiDescription);
    public bool HasSelectedTags => SelectedTags.Count > 0;

    // Derived display strings for the details panel
    public string SelectedDateText => SelectedMediaFile?.DateTaken?.ToString("MMMM d, yyyy") ?? "";
    public string SelectedDimensionsText => SelectedMediaFile is { Width: > 0, Height: > 0 } mf ? $"{mf.Width} × {mf.Height}" : "";
    public string SelectedFileSizeText => SelectedMediaFile != null ? FormatFileSize(SelectedMediaFile.FileSize) : "";
    public bool SelectedIsFavorite => SelectedMediaFile?.IsFavorite ?? false;

    // Active nav state for sidebar highlighting
    public bool IsAllPhotosActive => ActiveView == GalleryView.AllPhotos;
    public bool IsFavoritesActive => ActiveView == GalleryView.Favorites;

    public MainViewModel(IMediaFileRepository repo, IImportService import)
    {
        _repo = repo;
        _import = import;
        _ = LoadAsync();
    }

    // ── Data loading ────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            IEnumerable<MediaFile> photos = !string.IsNullOrWhiteSpace(SearchQuery)
                ? await _repo.SearchAsync(SearchQuery)
                : ActiveView == GalleryView.Favorites
                    ? await _repo.GetFavoritesAsync()
                    : await _repo.GetAllAsync();

            MediaFiles = new ObservableCollection<MediaFile>(photos);
            PhotoCount = MediaFiles.Count;
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally { IsLoading = false; }
    }

    partial void OnActiveViewChanged(GalleryView value)
    {
        OnPropertyChanged(nameof(IsAllPhotosActive));
        OnPropertyChanged(nameof(IsFavoritesActive));
        _ = LoadAsync();
    }

    partial void OnSearchQueryChanged(string value) => _ = LoadAsync();

    // ── Navigation commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void ShowAllPhotos()
    {
        SearchQuery = "";
        ActiveView = GalleryView.AllPhotos;
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        SearchQuery = "";
        ActiveView = GalleryView.Favorites;
    }

    // ── Import commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to import" };
        if (dlg.ShowDialog() != true) return;

        StatusText = "Importing…";
        var progress = new Progress<ImportProgress>(p =>
            StatusText = $"Importing {p.Processed}/{p.Total} — {Path.GetFileName(p.CurrentFile)}");
        var result = await _import.ImportFolderAsync(dlg.FolderName, true, progress);
        StatusText = $"Done — {result.Imported} imported, {result.Skipped} skipped";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ScanDrivesAsync()
    {
        var window = App.Services.GetRequiredService<ScanDrivesWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
        await LoadAsync();
    }

    // ── Favorite toggle ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedMediaFile == null) return;
        SelectedMediaFile.IsFavorite = !SelectedMediaFile.IsFavorite;
        await _repo.UpdateAsync(SelectedMediaFile);
        OnPropertyChanged(nameof(SelectedIsFavorite));
        // Keep grid in sync (item IsFavorite changed)
        var idx = MediaFiles.IndexOf(SelectedMediaFile);
        if (idx >= 0) { MediaFiles.RemoveAt(idx); MediaFiles.Insert(idx, SelectedMediaFile); }
        // Remove from view if browsing Favorites and just un-favorited
        if (ActiveView == GalleryView.Favorites && !SelectedMediaFile.IsFavorite)
            await LoadAsync();
    }

    // ── Selection ────────────────────────────────────────────────────────────

    partial void OnSelectedMediaFileChanged(MediaFile? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAiDescription));
        OnPropertyChanged(nameof(SelectedDateText));
        OnPropertyChanged(nameof(SelectedDimensionsText));
        OnPropertyChanged(nameof(SelectedFileSizeText));
        OnPropertyChanged(nameof(SelectedIsFavorite));
        SelectedTags.Clear();
        OnPropertyChanged(nameof(HasSelectedTags));
        if (value != null)
            _ = LoadTagsAsync(value.Id);
    }

    private async Task LoadTagsAsync(Guid id)
    {
        var full = await _repo.GetByIdAsync(id);
        SelectedTags.Clear();
        if (full?.Tags != null)
            foreach (var tag in full.Tags.Where(t => t.IsAIGenerated).OrderByDescending(t => t.Confidence))
                SelectedTags.Add(tag.Name);
        OnPropertyChanged(nameof(HasSelectedTags));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B"
    };
}
