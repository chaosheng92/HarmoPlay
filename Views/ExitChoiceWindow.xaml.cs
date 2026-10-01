using System.Windows;

namespace HarmoPlay.Views;

/// <summary>关闭窗口时的选择：退出程序 / 最小化到后台托盘。</summary>
public partial class ExitChoiceWindow : Window
{
    /// <summary>1 = 退出程序，2 = 最小化到后台。</summary>
    public int Choice { get; private set; } = 1;

    public bool Remember => ChkRemember.IsChecked == true;

    public ExitChoiceWindow()
    {
        InitializeComponent();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = 1;
        DialogResult = true;
    }

    private void OnBackground(object sender, RoutedEventArgs e)
    {
        Choice = 2;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
