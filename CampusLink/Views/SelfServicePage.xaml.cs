using System.Windows;
using System.Windows.Controls;
using CampusLink.ViewModels;

namespace CampusLink.Views
{
    public partial class SelfServicePage : UserControl
    {
        public SelfServicePage()
        {
            InitializeComponent();
        }

        private void SelfServicePasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as SelfServiceViewModel;
            if (viewModel != null) viewModel.Password = SelfServicePasswordBox.Password;
        }
    }
}
