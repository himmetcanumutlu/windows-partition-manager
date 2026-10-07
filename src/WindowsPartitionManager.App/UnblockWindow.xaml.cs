using System.Windows;

namespace WindowsPartitionManager.App;

public partial class UnblockWindow : Window
{
    public UnblockWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
