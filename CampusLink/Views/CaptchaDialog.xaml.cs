using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CampusLink.Views
{
    public partial class CaptchaDialog : Window
    {
        public CaptchaDialog(byte[] imageBytes, string title, string message)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(title)) TitleText.Text = title;
            MessageText.Text = message ?? "";

            if (imageBytes != null && imageBytes.Length > 0)
            {
                try
                {
                    using (var stream = new MemoryStream(imageBytes))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        CaptchaImage.Source = image;
                    }
                }
                catch
                {
                    MessageText.Text += "\n（验证码图片解码失败）";
                }
            }
            else
            {
                MessageText.Text += "\n（没有取到验证码图片，请到「协议诊断」页确认 validCodeUrl）";
            }

            Loaded += (sender, args) => CodeBox.Focus();
        }

        public string Code { get; private set; } = "";

        private void Ok_OnClick(object sender, RoutedEventArgs e)
        {
            Code = (CodeBox.Text ?? "").Trim();
            DialogResult = true;
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e)
        {
            Code = "";
            DialogResult = false;
        }
    }
}
