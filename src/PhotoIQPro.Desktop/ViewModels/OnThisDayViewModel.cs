using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

/// <summary>One year's worth of photos for "On This Day".</summary>
public class OnThisDayGroup
{
    public int                   Year   { get; init; }
    public int                   Count  => Photos.Count;
    public List<MediaFile>       Photos { get; init; } = [];
    public string                Label  => $"{Year}  ·  {Count} photo{(Count == 1 ? "" : "s")}";
}

/// <summary>
/// Drives the "On This Day" view — groups library photos by the same
/// month+day across prior years and exposes them as a flat collection of year groups.
/// </summary>
public partial class OnThisDayViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<OnThisDayGroup> _groups = [];
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _headingText = "";
    [ObservableProperty] private bool   _hasMemories;

    public event Action<MediaFile>?      PhotoSelected;
    public event Action<OnThisDayGroup>? SaveGroupAsAlbumRequested;

    public async Task LoadAsync(IReadOnlyList<MediaFile> allPhotos, DateTime today)
    {
        IsLoading = true;
        Groups.Clear();

        HeadingText = today.ToString("MMMM d");

        // Group by year descending.
        var byYear = allPhotos
            .GroupBy(p => p.DateTaken!.Value.Year)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var g in byYear)
            Groups.Add(new OnThisDayGroup
            {
                Year   = g.Key,
                Photos = g.OrderBy(p => p.DateTaken!.Value.TimeOfDay).ToList(),
            });

        HasMemories = Groups.Count > 0;
        IsLoading   = false;
    }

    public void SelectPhoto(MediaFile photo) => PhotoSelected?.Invoke(photo);

    [RelayCommand]
    private void SaveGroupAsAlbum(OnThisDayGroup? group)
    {
        if (group != null)
            SaveGroupAsAlbumRequested?.Invoke(group);
    }
}
