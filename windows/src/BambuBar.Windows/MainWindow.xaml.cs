using System.Windows;
using BambuBar.Core;

namespace BambuBar;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Bambu Bar {AppVersion.Marketing}";
    }
}
