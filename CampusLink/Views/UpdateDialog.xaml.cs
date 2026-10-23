using System.Windows;
using Wpf.Ui.Controls;

namespace CampusLink.Views
{
    public partial class UpdateDialog : FluentWindow
    {
        public UpdateDialog()
        {
            InitializeComponent();
        }

        private void Close_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
