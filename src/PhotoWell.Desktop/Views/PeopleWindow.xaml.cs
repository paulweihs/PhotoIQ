using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// Window for managing identified people and face recognition results.
/// </summary>
/// <remarks>
/// Shows all people detected in photos (via face detection and recognition).
/// Users can:
/// - View all faces for each identified person
/// - Edit person names and merge identities
/// - Review face-to-person matches and correct misidentifications
/// - Delete persons from the library
///
/// Faces are organized by person and displayed in a grid with face crops.
/// The PeopleViewModel handles all data loading, merging, and persistence.
/// </remarks>
public partial class PeopleWindow : Window
{
    /// <summary>Initializes a new instance of the PeopleWindow.</summary>
    /// <param name="vm">The PeopleViewModel that loads and manages person data and face recognition.</param>
    public PeopleWindow(PeopleViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    /// <summary>Handles keyboard shortcuts for person name editing: Enter to confirm, Escape to cancel.</summary>
    /// <remarks>
    /// The person name is extracted from the TextBox tag (PersonCardViewModel).
    /// This allows per-row keyboard handling without modal dialogs.
    /// </remarks>
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not PersonCardViewModel card) return;
        var vm = (PeopleViewModel)DataContext;

        if (e.Key == Key.Enter)
        {
            _ = vm.CommitEditCommand.ExecuteAsync(card);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelEditCommand.Execute(card);
            e.Handled = true;
        }
    }
}
