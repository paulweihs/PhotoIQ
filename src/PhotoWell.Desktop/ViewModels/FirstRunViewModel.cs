using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Desktop.Models;
using PhotoWell.Desktop.Services;

namespace PhotoWell.Desktop.ViewModels;

public enum DotState { Inactive, Active, Completed }

/// <summary>Per-dot state for the step indicator row.</summary>
public partial class DotViewModel : ObservableObject
{
    public int Index { get; }

    [ObservableProperty] private DotState _state = DotState.Inactive;

    public bool IsActive    => State == DotState.Active;
    public bool IsCompleted => State == DotState.Completed;

    partial void OnStateChanged(DotState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsCompleted));
    }

    public DotViewModel(int index) => Index = index;
}

/// <summary>
/// ViewModel for the first-run onboarding window.
/// Navigation, progress fraction, step dots, and card content.
/// After <c>ShowDialog()</c> returns, check <see cref="UserWantsToAddFolder"/>
/// to decide whether to invoke ScanDrivesCommand on MainViewModel.
/// </summary>
public partial class FirstRunViewModel : ObservableObject
{
    private readonly OnboardingService _onboarding;

    [ObservableProperty] private int _currentIndex;

    public int      TotalCards          => OnboardingService.AllCards.Length;
    public CardData CurrentCard         => OnboardingService.AllCards[CurrentIndex];
    public bool     IsLastCard          => CurrentIndex == TotalCards - 1;
    public bool     IsNotLastCard       => !IsLastCard;
    public string   ContinueButtonLabel => IsLastCard ? "Add My First Folder" : "Continue";

    /// <summary>Set to true when the user clicks the final-card CTA ("Add My First Folder").</summary>
    public bool UserWantsToAddFolder { get; private set; }

    /// <summary>Raised when the window should close (Skip or final CTA).</summary>
    public event Action? RequestClose;

    public ObservableCollection<DotViewModel> Dots { get; } = [];

    public FirstRunViewModel(OnboardingService onboarding)
    {
        _onboarding = onboarding;

        for (int i = 0; i < TotalCards; i++)
            Dots.Add(new DotViewModel(i));

        // Initialise first dot as active
        Dots[0].State = DotState.Active;
    }

    partial void OnCurrentIndexChanged(int value)
    {
        // Sync dot states
        for (int i = 0; i < Dots.Count; i++)
        {
            Dots[i].State = i < value  ? DotState.Completed
                          : i == value ? DotState.Active
                          :              DotState.Inactive;
        }

        OnPropertyChanged(nameof(CurrentCard));
        OnPropertyChanged(nameof(IsLastCard));
        OnPropertyChanged(nameof(IsNotLastCard));
        OnPropertyChanged(nameof(ContinueButtonLabel));
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLastCard)
        {
            Complete(addFolder: true);
        }
        else
        {
            CurrentIndex++;
        }
    }

    [RelayCommand]
    private void Skip() => Complete(addFolder: false);

    [RelayCommand]
    private void GoToCard(int index)
    {
        if (index >= 0 && index < TotalCards)
            CurrentIndex = index;
    }

    private void Complete(bool addFolder)
    {
        _onboarding.MarkOnboardingComplete();
        UserWantsToAddFolder = addFolder;
        RequestClose?.Invoke();
    }
}
