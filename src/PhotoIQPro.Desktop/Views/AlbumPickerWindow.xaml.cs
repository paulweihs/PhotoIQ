using System.Windows;
using System.Windows.Controls;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Dialog window for selecting an album to add photos to, or creating a new album.
/// </summary>
/// <remarks>
/// Users can either:
/// 1. Select an existing album from a list (organized by library/album hierarchy)
/// 2. Create a new album in an existing library
/// 3. Create a new album in a brand-new library (both created together)
///
/// The dialog returns results via properties:
/// - SelectedAlbumId/SelectedAlbumName: if an existing album was selected
/// - IsNewAlbum/NewAlbumName/NewAlbumLibraryId/NewLibraryName: if a new album was created
///
/// DialogResult=true indicates success; false if the user cancelled.
/// </remarks>
public partial class AlbumPickerWindow : Window
{
    /// <summary>The ID of the selected existing album (null if creating new).</summary>
    public Guid? SelectedAlbumId { get; private set; }

    /// <summary>The display name of the selected existing album (library / album format).</summary>
    public string SelectedAlbumName { get; private set; } = "";

    /// <summary>True if the user chose to create a new album; false if selecting an existing one.</summary>
    public bool IsNewAlbum { get; private set; }

    /// <summary>The name of the new album to create (if IsNewAlbum=true).</summary>
    public string NewAlbumName { get; private set; } = "";

    /// <summary>The library ID for the new album (null if NewLibraryName should also be created).</summary>
    public Guid? NewAlbumLibraryId { get; private set; }

    /// <summary>The name of the new library to create (if NewAlbumLibraryId=null).</summary>
    public string NewLibraryName { get; private set; } = "";

    private record AlbumItem(Guid Id, string AlbumName);

    // Parallel lists for the Library ComboBox — plain strings for display,
    // matching Guid? for lookup. null entry = "create new library" sentinel.
    private readonly List<Guid?> _libraryIds = [];

    public AlbumPickerWindow(IReadOnlyList<Library> libraries)
    {
        InitializeComponent();

        // ── Existing albums list ────────────────────────────────────────────
        var albumItems = libraries
            .SelectMany(l => l.Albums.OrderBy(a => a.Name)
                .Select(a => new AlbumItem(a.Id, $"{l.Name}  /  {a.Name}")))
            .ToList();

        AlbumList.ItemsSource = albumItems.Select(a => a.AlbumName).ToList();
        // Keep original entries for Id lookup by index.
        _albumEntries = albumItems;

        // ── Library ComboBox ────────────────────────────────────────────────
        var displayNames = new List<string>();
        foreach (var lib in libraries.OrderBy(l => l.Name))
        {
            _libraryIds.Add(lib.Id);
            displayNames.Add(lib.Name);
        }
        _libraryIds.Add(null);                          // sentinel = new library
        displayNames.Add("＋  New library…");

        LibraryCombo.ItemsSource   = displayNames;
        LibraryCombo.SelectedIndex = 0;
    }

    private readonly List<AlbumItem> _albumEntries;

    private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = AlbumList.SelectedIndex >= 0;
    }

    private void AlbumList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AlbumList.SelectedIndex < 0) return;
        CommitExistingSelection();
    }

    private void LibraryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isNew = LibraryCombo.SelectedIndex >= 0
                 && _libraryIds.Count > 0
                 && LibraryCombo.SelectedIndex < _libraryIds.Count
                 && _libraryIds[LibraryCombo.SelectedIndex] == null;

        NewLibraryRow.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        UpdateCreateButton();
    }

    private void NewField_TextChanged(object sender, TextChangedEventArgs e) => UpdateCreateButton();

    private void UpdateCreateButton()
    {
        var albumName = NewAlbumNameBox.Text.Trim();
        var idx       = LibraryCombo.SelectedIndex;
        var isNewLib  = idx >= 0 && idx < _libraryIds.Count && _libraryIds[idx] == null;
        var libNameOk = !isNewLib || !string.IsNullOrWhiteSpace(NewLibraryNameBox.Text.Trim());
        CreateButton.IsEnabled = albumName.Length > 0 && libNameOk;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => CommitExistingSelection();

    private void CommitExistingSelection()
    {
        var idx = AlbumList.SelectedIndex;
        if (idx < 0 || idx >= _albumEntries.Count) return;
        var entry         = _albumEntries[idx];
        SelectedAlbumId   = entry.Id;
        SelectedAlbumName = entry.AlbumName;
        IsNewAlbum        = false;
        DialogResult      = true;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var albumName = NewAlbumNameBox.Text.Trim();
        if (string.IsNullOrEmpty(albumName)) return;

        var idx = LibraryCombo.SelectedIndex;
        IsNewAlbum   = true;
        NewAlbumName = albumName;

        if (idx >= 0 && idx < _libraryIds.Count && _libraryIds[idx] != null)
        {
            NewAlbumLibraryId = _libraryIds[idx];
        }
        else
        {
            NewAlbumLibraryId = null;
            NewLibraryName    = NewLibraryNameBox.Text.Trim();
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
