using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Desktop.Views;

namespace PhotoIQPro.Desktop.ViewModels;

/// <summary>Node in the library tree — either a Library header or an Album leaf.</summary>
public partial class LibraryNodeViewModel : ObservableObject
{
    public bool IsLibrary { get; }
    public Guid Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool   _isActive;

    public Guid? LibraryId { get; }          // null for library nodes
    public int PhotoCount { get; set; }

    public ObservableCollection<LibraryNodeViewModel> Children { get; } = [];
    public bool HasAlbums => Children.Count > 0;

    public LibraryNodeViewModel(Library lib)
    {
        IsLibrary = true;
        Id = lib.Id;
        _name = lib.Name;
    }

    public LibraryNodeViewModel(Album album)
    {
        IsLibrary = false;
        Id = album.Id;
        _name = album.Name;
        LibraryId = album.LibraryId;
    }
}

public partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryRepository _repo;

    public ObservableCollection<LibraryNodeViewModel> Nodes { get; } = [];

    [ObservableProperty] private LibraryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _statusText = "";

    public bool CanCreateAlbum    => SelectedNode != null;
    public bool CanDeleteOrRename => SelectedNode != null;
    public bool IsAlbumSelected   => SelectedNode?.IsLibrary == false;
    public bool HasNoLibraries    => Nodes.Count == 0;

    /// <summary>Raised when the user wants to view an album in the main gallery.</summary>
    public event Action<Guid, string>? ViewAlbumRequested;

    public LibraryViewModel(ILibraryRepository repo)
    {
        _repo = repo;
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var counts = await _repo.GetAlbumPhotoCountsAsync();
        var libs   = await _repo.GetAllAsync();

        Nodes.Clear();
        foreach (var lib in libs)
        {
            var libNode = new LibraryNodeViewModel(lib);
            foreach (var album in lib.Albums.OrderBy(a => a.Name))
            {
                var albumNode = new LibraryNodeViewModel(album)
                {
                    PhotoCount = counts.TryGetValue(album.Id, out var c) ? c : 0
                };
                libNode.Children.Add(albumNode);
            }
            Nodes.Add(libNode);
        }
        OnPropertyChanged(nameof(HasNoLibraries));
    }

    partial void OnSelectedNodeChanged(LibraryNodeViewModel? value)
    {
        OnPropertyChanged(nameof(CanCreateAlbum));
        OnPropertyChanged(nameof(CanDeleteOrRename));
        OnPropertyChanged(nameof(IsAlbumSelected));
    }

    [RelayCommand]
    private async Task NewLibraryAsync()
    {
        var name = PromptName("New Library", "Library name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var lib = await _repo.CreateLibraryAsync(name.Trim());
        var node = new LibraryNodeViewModel(lib);
        Nodes.Add(node);
        SelectedNode = node;
        OnPropertyChanged(nameof(HasNoLibraries));
    }

    [RelayCommand]
    private async Task NewAlbumAsync()
    {
        if (SelectedNode == null) return;
        var libraryId = SelectedNode.IsLibrary ? SelectedNode.Id : SelectedNode.LibraryId!.Value;
        var libNode   = SelectedNode.IsLibrary ? SelectedNode : Nodes.FirstOrDefault(n => n.Id == libraryId);
        if (libNode == null) return;

        var name = PromptName("New Album", "Album name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var album = await _repo.CreateAlbumAsync(libraryId, name.Trim());
        var albumNode = new LibraryNodeViewModel(album) { PhotoCount = 0 };
        libNode.Children.Add(albumNode);
        SelectedNode = albumNode;
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedNode == null) return;
        var newName = PromptName("Rename", "New name:", SelectedNode.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedNode.Name) return;

        if (SelectedNode.IsLibrary)
            await _repo.RenameLibraryAsync(SelectedNode.Id, newName.Trim());
        else
            await _repo.RenameAlbumAsync(SelectedNode.Id, newName.Trim());

        SelectedNode.Name = newName.Trim();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedNode == null) return;

        var kind = SelectedNode.IsLibrary ? "library" : "album";
        var msg  = SelectedNode.IsLibrary
            ? $"Delete library \"{SelectedNode.Name}\" and all its albums?\n\nPhotos are NOT deleted from disk."
            : $"Delete album \"{SelectedNode.Name}\"?\n\nPhotos are NOT deleted from disk.";

        var result = System.Windows.MessageBox.Show(msg, $"Delete {kind}",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        if (SelectedNode.IsLibrary)
        {
            await _repo.DeleteLibraryAsync(SelectedNode.Id);
            Nodes.Remove(SelectedNode);
        }
        else
        {
            var libNode = Nodes.FirstOrDefault(n => n.Id == SelectedNode.LibraryId);
            await _repo.DeleteAlbumAsync(SelectedNode.Id);
            libNode?.Children.Remove(SelectedNode);
        }
        SelectedNode = null;
        OnPropertyChanged(nameof(HasNoLibraries));
    }

    [RelayCommand]
    private void ViewInGallery()
    {
        if (SelectedNode?.IsLibrary == false)
            ViewAlbumRequested?.Invoke(SelectedNode.Id, SelectedNode.Name);
    }

    private static string? PromptName(string title, string label, string current = "")
    {
        var dialog = new TextInputDialog(title, label, current);
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }
}
