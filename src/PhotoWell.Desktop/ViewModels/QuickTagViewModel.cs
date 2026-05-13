using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// ViewModel for the QuickTagWindow — a minimal floating window opened from
/// the Windows Explorer context menu ("Add Tag…") or from the main app.
/// Supports tagging one or more files simultaneously.
/// </summary>
public partial class QuickTagViewModel : ObservableObject
{
    private readonly IServiceProvider    _services;
    private readonly IReadOnlyList<string> _filePaths;

    [ObservableProperty] private string  _newTagText    = "";
    [ObservableProperty] private string  _statusMessage = "";
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private object? _thumbnail;

    /// <summary>Tags applied to ALL selected files (intersection when multi-file).</summary>
    public ObservableCollection<TagViewModel> SharedTags { get; } = [];

    /// <summary>Display title — filename for single file, count for multi-file.</summary>
    public string Title => _filePaths.Count == 1
        ? Path.GetFileName(_filePaths[0])
        : $"{_filePaths.Count} photos selected";

    public string SubTitle => _filePaths.Count == 1
        ? _filePaths[0]
        : string.Join(", ", _filePaths.Select(Path.GetFileName));

    public bool IsMultiFile => _filePaths.Count > 1;

    public event Action? RequestClose;

    public QuickTagViewModel(IReadOnlyList<string> filePaths, IServiceProvider services)
    {
        _services  = services;
        _filePaths = filePaths;
        _ = LoadAsync();
    }

    // Convenience constructor for single file (shell extension --tag path)
    public QuickTagViewModel(string filePath, IServiceProvider services)
        : this([filePath], services) { }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading…";
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

            // Load tags shared across all selected files.
            SharedTags.Clear();
            List<TagViewModel>? commonTags = null;

            foreach (var path in _filePaths)
            {
                var mf = await repo.GetByPathAsync(path);
                if (mf is null) continue;

                var fileTags = mf.Tags
                    .Select(t => new TagViewModel(t.Id, t.Name, t.IsAIGenerated))
                    .ToList();

                if (commonTags is null)
                    commonTags = fileTags;
                else
                    commonTags = commonTags.IntersectBy(fileTags.Select(t => t.Id), t => t.Id).ToList();
            }

            if (commonTags is not null)
                foreach (var t in commonTags.OrderByDescending(t => t.IsAIGenerated).ThenBy(t => t.Name))
                    SharedTags.Add(t);

            StatusMessage = IsMultiFile
                ? $"Tags shown are common to all {_filePaths.Count} selected photos."
                : "";

            // Load thumbnail for single-file mode.
            if (_filePaths.Count == 1)
            {
                var mf = await repo.GetByPathAsync(_filePaths[0]);
                var thumbPath = mf?.ThumbnailLarge ?? mf?.ThumbnailMedium ?? mf?.ThumbnailSmall;
                if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                {
                    Thumbnail = await Task.Run(() =>
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource   = new Uri(thumbPath, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickTagViewModel.LoadAsync: {ex.Message}");
            StatusMessage = $"Error loading photos: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagText.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        IsBusy = true;
        int applied = 0;
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

            var tag = await repo.GetOrCreateTagAsync(
                name, name.ToLowerInvariant(), TagCategory.Custom, false, 0f);

            foreach (var path in _filePaths)
            {
                var mf = await repo.GetByPathAsync(path);
                if (mf is null) continue;

                if (!mf.Tags.Any(t => t.Id == tag.Id))
                {
                    mf.Tags.Add(tag);
                    await repo.UpdateAsync(mf);
                    applied++;
                }
            }

            // Add to shared tags display if not already shown.
            if (SharedTags.All(t => t.Id != tag.Id))
                SharedTags.Add(new TagViewModel(tag.Id, tag.Name, false));

            NewTagText    = "";
            StatusMessage = applied > 0
                ? $"Tag \"{name}\" added to {applied} photo{(applied == 1 ? "" : "s")}."
                : $"Tag \"{name}\" was already applied.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickTagViewModel.AddTagAsync: {ex.Message}");
            StatusMessage = $"Failed to add tag: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveTagAsync(TagViewModel tag)
    {
        IsBusy = true;
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

            foreach (var path in _filePaths)
            {
                var mf = await repo.GetByPathAsync(path);
                if (mf is null) continue;
                if (mf.Tags.Any(t => t.Id == tag.Id))
                    await repo.RemoveTagFromPhotoAsync(mf.Id, tag.Id);
            }

            SharedTags.Remove(tag);
            StatusMessage = $"Tag \"{tag.Name}\" removed.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickTagViewModel.RemoveTagAsync: {ex.Message}");
            StatusMessage = $"Failed to remove tag: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
