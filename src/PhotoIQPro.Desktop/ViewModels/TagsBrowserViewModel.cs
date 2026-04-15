using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Desktop.ViewModels;

public class TagEntry
{
    public string Name      { get; init; } = "";
    public int    PhotoCount { get; init; }
}

public partial class TagsBrowserViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    [ObservableProperty] private ObservableCollection<TagEntry> _tags = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText = "";

    private IReadOnlyList<TagEntry> _allTags = [];

    public event Action<string>? RequestFilterByTag;
    public event Action?         RequestClose;

    public TagsBrowserViewModel(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    public async Task LoadAsync()
    {
        IsLoading = true;
        Tags.Clear();

        using var scope = _scopeFactory.CreateScope();
        var repo   = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var counts = await repo.GetTagPhotoCountsAsync();

        _allTags = counts
            .Where(kv => !string.IsNullOrEmpty(kv.Key))
            .Select(kv => new TagEntry { Name = kv.Key, PhotoCount = kv.Value })
            .OrderByDescending(t => t.PhotoCount)
            .ThenBy(t => t.Name)
            .ToList();

        ApplyFilter();
        UpdateStatus();
        IsLoading = false;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allTags
            : _allTags.Where(t => t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        Tags.Clear();
        foreach (var t in filtered) Tags.Add(t);
        UpdateStatus();
    }

    [RelayCommand]
    private void FilterByTag(TagEntry tag) => RequestFilterByTag?.Invoke(tag.Name);

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private void UpdateStatus()
    {
        StatusText = Tags.Count == 0
            ? "No tags found."
            : $"{Tags.Count} tag{(Tags.Count == 1 ? "" : "s")}";
    }
}
