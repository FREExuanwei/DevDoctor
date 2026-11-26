using System.Windows;

namespace DevDoctor.App;

public partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(string title, string message)
    {
        InitializeComponent(); Title = title; Heading.Text = title; Message.Text = message;
        var available = SystemParameters.WorkArea; Width = Math.Min(Width, available.Width); Height = Math.Min(Height, available.Height);
    }
    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
