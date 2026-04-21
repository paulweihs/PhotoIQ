using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Desktop.ViewModels;

/// <summary>A tag name and the number of photos tagged with it.</summary>
/// <remarks>Used by TagsBrowserViewModel to display tag counts in the tag browser window.</remarks>
public class TagEntry
{
    /// <summary>The tag's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The number of non-excluded photos tagged with this tag.</summary>
    public int PhotoCount { get; init; }
}

/// <summary>
/// ViewModel for the tag browser window, showing all tags used in the library with photo counts.
/// </summary>
/// <remarks>
/// Users can search for tags by name and click one to filter the main gallery to show only
/// photos with that tag. Tags are loaded on window open and sorted by frequency (most-used first).
/// </remarks>
public partial class TagsBrowserViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>The filtered list of tags displayed in the browser (matches current search query).</summary>
    [ObservableProperty] private ObservableCollection<TagEntry> _tags = [];

    /// <summary>User's search query to filter tags by name (case-insensitive substring match).</summary>
    [ObservableProperty] private string _searchText = "";

    /// <summary>True while tags are loading from the database.</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>Status message showing the number of tags displayed (e.g., "42 tags" or "No tags found.").</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>All tags loaded from the database (before search filtering). Cached for quick filter re-application.</summary>
    private IReadOnlyList<TagEntry> _allTags = [];

    /// <summary>Raised when the user clicks a tag to filter the gallery by that tag.</summary>
    public event Action<string>? RequestFilterByTag;

    /// <summary>Raised when the user closes the tag browser window.</summary>
    public event Action? RequestClose;

    /// <summary>Initializes a new instance of the TagsBrowserViewModel.</summary>
    /// <param name="scopeFactory">Factory for creating DI scopes to access the repository.</param>
    public TagsBrowserViewModel(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    /// <summary>Loads all tags from the database, sorted by photo count (descending).</summary>
    /// <remarks>Called when the tag browser window opens. Sets IsLoading=true during the operation.</remarks>
    public async Task LoadAsync()
    {
        IsLoading = true;
        Tags.Clear();

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
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

    /// <summary>Triggered when SearchText changes; re-applies the tag name filter.</summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Filters tags by search query and updates the displayed list.</summary>
    /// <remarks>Performs case-insensitive substring matching on tag names.</remarks>
    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allTags
            : _allTags.Where(t => t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        Tags.Clear();
        foreach (var t in filtered) Tags.Add(t);
        UpdateStatus();
    }

    /// <summary>Invoked when the user clicks a tag to filter the gallery.</summary>
    /// <remarks>Raises RequestFilterByTag event with the tag name.</remarks>
    [RelayCommand]
    private void FilterByTag(TagEntry tag) => RequestFilterByTag?.Invoke(tag.Name);

    /// <summary>Closes the tag browser window.</summary>
    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private void UpdateStatus()
    {
        StatusText = Tags.Count == 0
            ? "No tags found."
            : $"{Tags.Count} tag{(Tags.Count == 1 ? "" : "s")}";
    }
}
