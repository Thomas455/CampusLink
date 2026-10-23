using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CampusLink.ViewModels;

namespace CampusLink.Views
{
    public partial class AccountsPage : UserControl
    {
        private MainViewModel _viewModel;

        public AccountsPage()
        {
            InitializeComponent();
            DataContextChanged += (sender, args) => Hook(args.NewValue as MainViewModel);
        }

        private void Hook(MainViewModel viewModel)
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _viewModel = viewModel;
            if (_viewModel != null) _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        }

        private void ViewModel_OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedAccount) ||
                e.PropertyName == nameof(MainViewModel.IsNewAccount))
            {
                AccountPasswordBox.Password = "";
            }
        }

        private void AccountPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            _viewModel?.SetEditorPassword(AccountPasswordBox.Password);
        }
    }
}
