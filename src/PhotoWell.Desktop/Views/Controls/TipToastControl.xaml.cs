using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views.Controls;

public partial class TipToastControl : UserControl
{
    private TipToastViewModel? _vm;

    public TipToastControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as TipToastViewModel;
        if (_vm is null) return;

        _vm.ShowRequested    += PlayShowAnimation;
        _vm.DismissRequested += PlayHideAnimation;
    }

    private void PlayShowAnimation()
    {
        Dispatcher.InvokeAsync(() =>
        {
            Visibility = Visibility.Visible;

            // Reset transforms before animating in
            ScaleTransform.ScaleX = 1;
            ScaleTransform.ScaleY = 1;
            RootBorder.Opacity    = 0;

            var sb = (Storyboard)Resources["ShowStoryboard"];
            sb.Begin(this);
        });
    }

    private void PlayHideAnimation()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var sb = (Storyboard)Resources["HideStoryboard"];
            sb.Completed += (_, _) => Visibility = Visibility.Collapsed;
            sb.Begin(this);
        });
    }

    private void OnDisableTipsChecked(object sender, RoutedEventArgs e)
    {
        _vm?.DisableTipsCommand.Execute(null);
    }
}
