using CommunityToolkit.Mvvm.ComponentModel;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// View model for a single person card in the People browser grid.
/// </summary>
public partial class PersonCardViewModel : ObservableObject
{
    public Guid    PersonId      { get; }
    public string? KeyFacePath   { get; }

    [ObservableProperty] private string  _displayName;
    [ObservableProperty] private int     _photoCount;
    [ObservableProperty] private bool    _isEditing;
    [ObservableProperty] private string  _editName;
    [ObservableProperty] private bool    _isSelected;   // used during merge selection
    [ObservableProperty] private bool    _isFavorite;

    public bool IsNamed { get; private set; }

    public PersonCardViewModel(Person person, string? keyFacePath, int photoCount)
    {
        PersonId    = person.Id;
        KeyFacePath = keyFacePath;
        IsNamed     = person.IsNamed;
        _isFavorite = person.IsFavorite;
        _photoCount = photoCount;
        _displayName = person.IsNamed && !string.IsNullOrWhiteSpace(person.Name)
            ? person.Name
            : "Unknown";
        _editName = _displayName == "Unknown" ? "" : _displayName;
    }

    public void ApplyName(string name)
    {
        DisplayName = name;
        IsNamed     = true;
        EditName    = name;
        IsEditing   = false;
    }
}
