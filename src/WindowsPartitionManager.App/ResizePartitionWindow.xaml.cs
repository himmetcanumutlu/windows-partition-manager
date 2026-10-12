using System.Windows;

namespace WindowsPartitionManager.App;

public partial class ResizePartitionWindow : Window
{
    public ResizePartitionWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
