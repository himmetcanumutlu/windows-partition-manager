using System.Windows;

namespace WindowsPartitionManager.App;

public partial class DestructiveActionWindow : Window
{
    public DestructiveActionWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
