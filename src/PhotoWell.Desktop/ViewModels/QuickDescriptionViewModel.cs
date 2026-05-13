using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// ViewModel for the QuickDescriptionWindow — a minimal floating window opened from
/// the Windows Explorer context menu (--describe "path") or from the main app.
/// </summary>
/// <remarks>
/// Loads the MediaFile record by file path, shows the AI description, allows editing,
/// saving the edited text back to the database, and re-queueing the photo for AI analysis.
/// </remarks>
public partial class QuickDescriptionViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private MediaFile?                _mediaFile;

    [ObservableProperty] private string  _filePath    = "";
    [ObservableProperty] private string  _fileName    = "";
    [ObservableProperty] private string  _description = "";
    [ObservableProperty] private object? _thumbnail;
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string  _statusMessage = "";

    public event Action? RequestClose;

    public QuickDescriptionViewModel(string filePath, IServiceProvider services)
    {
        _services  = services;
        FilePath   = filePath;
        FileName   = Path.GetFileName(filePath);
        _ = LoadAsync(filePath);
    }

    private async Task LoadAsync(string filePath)
    {
        IsBusy = true;
        StatusMessage = "Loading…";
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
            _mediaFile  = await repo.GetByPathAsync(filePath);

            if (_mediaFile is null)
            {
                StatusMessage = "Photo not found in library.";
                return;
            }

            Description = _mediaFile.UserDescription ?? _mediaFile.AiDescription ?? "";
            StatusMessage = "";

            // Load thumbnail asynchronously.
            var thumbPath = _mediaFile.ThumbnailLarge ?? _mediaFile.ThumbnailMedium ?? _mediaFile.ThumbnailSmall;
            if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
            {
                Thumbnail = await Task.Run(() =>
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption  = BitmapCacheOption.OnLoad;
                    bmp.UriSource    = new Uri(thumbPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickDescriptionViewModel.LoadAsync: {ex.Message}");
            StatusMessage = $"Error loading photo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_mediaFile is null) return;
        IsBusy = true;
        StatusMessage = "Saving…";
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

            // Reload to get a tracked entity, then update.
            var file = await repo.GetByIdAsync(_mediaFile.Id);
            if (file is null) return;

            file.UserDescription       = Description;
            file.DescriptionEditedAt   = DateTime.UtcNow;
            await repo.UpdateAsync(file);
            StatusMessage = "Saved.";

            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickDescriptionViewModel.SaveAsync: {ex.Message}");
            StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReanalyzeAsync()
    {
        if (_mediaFile is null) return;
        IsBusy = true;
        StatusMessage = "Queued for re-analysis…";
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();

            var file = await repo.GetByIdAsync(_mediaFile.Id);
            if (file is null) return;

            file.AnalysisStatus = AnalysisStatus.Pending;
            await repo.UpdateAsync(file);
            StatusMessage = "Photo queued for re-analysis. Close this window and the main app will process it.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"QuickDescriptionViewModel.ReanalyzeAsync: {ex.Message}");
            StatusMessage = $"Failed to queue re-analysis: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
