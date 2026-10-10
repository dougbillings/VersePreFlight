using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VersePreflight.App.Views;

public partial class CharactersView : UserControl
{
    public CharactersView() => InitializeComponent();

    private void OnResetView(object? sender, RoutedEventArgs e) => Viewer.ResetView();
}
