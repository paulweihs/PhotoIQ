using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Desktop.Models;
using PhotoWell.Desktop.Services;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// Drives the startup tip toast overlay.
/// Registered as a singleton in DI so the toast state survives across the session.
/// Call <see cref="Show"/> from App.xaml.cs after the 800 ms startup delay.
/// </summary>
public partial class TipToastViewModel : ObservableObject
{
    private readonly OnboardingService _onboarding;

    [ObservableProperty] private bool     _isVisible;
    [ObservableProperty] private TipData? _currentTip;

    // Raised by the control's code-behind to start animations; handled by TipToastControl.
    public event Action? ShowRequested;
    public event Action? DismissRequested;

    public TipToastViewModel(OnboardingService onboarding) => _onboarding = onboarding;

    /// <summary>
    /// Loads the next tip and signals the control to animate in.
    /// Called by App.xaml.cs after the 800 ms startup delay.
    /// </summary>
    public void Show()
    {
        CurrentTip = _onboarding.GetNextTip();
        IsVisible  = true;
        ShowRequested?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
        DismissRequested?.Invoke();
    }

    [RelayCommand]
    private async Task NextTipAsync()
    {
        // Animate out, wait briefly, then show the next tip.
        IsVisible = false;
        DismissRequested?.Invoke();
        await Task.Delay(350);
        Show();
    }

    [RelayCommand]
    private async Task DisableTipsAsync()
    {
        _onboarding.DisableTips();
        IsVisible = false;
        DismissRequested?.Invoke();
        await Task.CompletedTask;
    }
}
