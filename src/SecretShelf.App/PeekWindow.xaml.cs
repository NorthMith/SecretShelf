using System.Windows;
using System.Windows.Input;

namespace SecretShelf.App;

public partial class PeekWindow : Window
{
    public PeekWindow() => InitializeComponent();

    public event EventHandler? ExpandRequested;

    public void ShowAtCorner()
    {
        PlaceAtCorner();
        Show();
    }

    public void PlaceAtCorner()
    {
        Rect wa = SystemParameters.WorkArea;
        Left = wa.Right - Width;
        Top = wa.Bottom - Height - 12;
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);
}
