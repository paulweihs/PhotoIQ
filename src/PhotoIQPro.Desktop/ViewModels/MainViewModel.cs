using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Desktop.Views;

namespace PhotoIQPro.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IMediaFileRepository _repo;
    private readonly IImportService _import;

    [ObservableProperty] private ObservableCollection<MediaFile> _mediaFiles = [];
    [ObservableProperty] private MediaFile? _selectedMediaFile;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _photoCount;

    public bool IsEmpty => PhotoCount == 0;
    public bool HasSelection => SelectedMediaFile != null;

    public MainViewModel(IMediaFileRepository repo, IImportService import)
    {
        _repo = repo;
        _import = import;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var photos = await _repo.GetAllAsync();
        MediaFiles = new ObservableCollection<MediaFile>(photos);
        PhotoCount = MediaFiles.Count;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to import" };
        if (dlg.ShowDialog() == true)
        {
            StatusText = "Importing...";
            var progress = new Progress<ImportProgress>(p => StatusText = $"Importing: {p.Processed}/{p.Total}");
            var result = await _import.ImportFolderAsync(dlg.FolderName, true, progress);
            StatusText = $"Imported {result.Imported} photos";
            await LoadAsync();
        }
    }
    [RelayCommand]
    private void ScanDrives()
    {
        var window = App.Services.GetRequiredService<ScanDrivesWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // After dialog closes, refresh the media list if needed
        // (The import happens inside the dialog, so we just need to reload)
        // TODO: Add a refresh/reload command here once the grid is populated
    }
    partial void OnSelectedMediaFileChanged(MediaFile? value) => OnPropertyChanged(nameof(HasSelection));
}
