using System.Windows.Controls;
using System.Windows.Input;

namespace DevDoctor.App.Views;
public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    public void FocusQuery() { if (IsVisible) { QueryInput.Focus(); Keyboard.Focus(QueryInput); QueryInput.SelectAll(); } }
}
