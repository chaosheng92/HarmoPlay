using System.Windows;

namespace HarmoPlay.Views;

public partial class InputWindow : Window
{
    public InputWindow(string title, string label, string value)
    {
        InitializeComponent();
        Title = title;
        TxtTitle.Text = label;
        TxtValue.Text = value;
        Loaded += (_, _) =>
        {
            TxtValue.Focus();
            TxtValue.SelectAll();
        };
    }

    public string Value => TxtValue.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtValue.Text)) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
