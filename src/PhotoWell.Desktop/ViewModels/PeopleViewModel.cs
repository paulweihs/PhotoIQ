using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Desktop.ViewModels;

public partial class PeopleViewModel : ObservableObject
{
    private readonly IPersonRepository       _personRepo;
    private readonly IFaceRecognitionService _recognition;

    [ObservableProperty] private ObservableCollection<PersonCardViewModel> _people = [];
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool   _isMergeMode;

    // First card selected during a merge operation (the target we keep).
    private PersonCardViewModel? _mergeTarget;

    public event Action<Guid>? RequestFilterByPerson;
    public event Action?       RequestClose;

    public PeopleViewModel(IPersonRepository personRepo, IFaceRecognitionService recognition)
    {
        _personRepo  = personRepo;
        _recognition = recognition;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        IsLoading = true;
        People.Clear();

        var summaries = await _personRepo.GetAllSummariesAsync();
        foreach (var s in summaries)
            People.Add(new PersonCardViewModel(s.Person, s.KeyFaceThumbnailPath, s.PhotoCount));

        UpdateStatus();
        IsLoading = false;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void BeginEdit(PersonCardViewModel card)
    {
        card.EditName  = card.IsNamed ? card.DisplayName : "";
        card.IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitEdit(PersonCardViewModel card)
    {
        var name = card.EditName.Trim();
        card.IsEditing = false;
        if (string.IsNullOrEmpty(name)) return;

        var person = await _personRepo.GetByIdAsync(card.PersonId);
        if (person == null) return;

        person.Name           = name;
        person.NormalizedName = name.ToLowerInvariant();
        person.IsNamed        = true;
        await _personRepo.UpdateAsync(person);

        card.ApplyName(name);
        await _recognition.InvalidateCacheAsync();
    }

    [RelayCommand]
    private void CancelEdit(PersonCardViewModel card) => card.IsEditing = false;

    [RelayCommand]
    private async Task Hide(PersonCardViewModel card)
    {
        await _personRepo.HideAsync(card.PersonId);
        People.Remove(card);
        await _recognition.InvalidateCacheAsync();
        UpdateStatus();
    }

    [RelayCommand]
    private async Task Delete(PersonCardViewModel card)
    {
        var answer = MessageBox.Show(
            $"Permanently delete \"{card.DisplayName}\" and remove all face assignments?\n\nThis cannot be undone.",
            "Delete Person",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await _personRepo.DeleteAsync(card.PersonId);
        await _recognition.InvalidateCacheAsync();
        People.Remove(card);
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ToggleFavorite(PersonCardViewModel card)
    {
        await _personRepo.ToggleFavoriteAsync(card.PersonId);
        card.IsFavorite = !card.IsFavorite;
    }

    [RelayCommand]
    private void ViewPhotos(PersonCardViewModel card)
        => RequestFilterByPerson?.Invoke(card.PersonId);

    // ── Merge ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleMergeMode()
    {
        IsMergeMode = !IsMergeMode;
        if (!IsMergeMode) ClearMergeSelection();
    }

    /// <summary>
    /// Two-click merge: first click selects the target (the person we keep),
    /// second click selects the source (absorbed into the target).
    /// </summary>
    [RelayCommand]
    private async Task SelectForMerge(PersonCardViewModel card)
    {
        if (!IsMergeMode) return;

        if (_mergeTarget == null)
        {
            _mergeTarget    = card;
            card.IsSelected = true;
            StatusText = $"Merging into \"{card.DisplayName}\" — click another person to absorb them.";
            return;
        }

        if (card.PersonId == _mergeTarget.PersonId)
        {
            ClearMergeSelection();
            return;
        }

        var source = card;
        var target = _mergeTarget;
        ClearMergeSelection();
        IsMergeMode = false;

        await _personRepo.MergeIntoAsync(source.PersonId, target.PersonId);
        await _recognition.InvalidateCacheAsync();

        People.Remove(source);
        target.PhotoCount = await _personRepo.GetPhotoCountAsync(target.PersonId);
        UpdateStatus();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ClearMergeSelection()
    {
        if (_mergeTarget != null) _mergeTarget.IsSelected = false;
        _mergeTarget = null;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText = People.Count == 0
            ? "No people detected yet. Import photos to start building your People library."
            : $"{People.Count} {(People.Count == 1 ? "person" : "people")} · click a card to view their photos";
    }
}
