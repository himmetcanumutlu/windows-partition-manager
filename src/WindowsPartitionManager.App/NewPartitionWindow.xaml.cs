using System.Windows;

namespace WindowsPartitionManager.App;

public partial class NewPartitionWindow : Window
{
    public NewPartitionWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
