using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;

namespace XboxShellApp
{
    public partial class LoginPage : UserControl
    {
        string accountsRoot = System.IO.Path.Combine("Data", "Accounts");
        private MainWindow _mainWindow;

        public LoginPage(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            RefreshAccounts();
            AddAccountBtn.Click += AddAccountBtn_Click;
        }

        private void RefreshAccounts()
        {
            AccountsPanel.Items.Clear();
            if (!Directory.Exists(accountsRoot))
                Directory.CreateDirectory(accountsRoot);

            var accounts = Directory.GetDirectories(accountsRoot);
            if (accounts.Length == 0)
            {
                AddAccountBtn.Visibility = Visibility.Visible;
            }
            else
            {
                AddAccountBtn.Visibility = Visibility.Collapsed;
                foreach (var dir in accounts)
                {
                    string user = System.IO.Path.GetFileName(dir);
                    string imgPath = System.IO.Path.Combine(dir, "profile.png");
                    if (!File.Exists(imgPath))
                    {
                        var bytes = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAQAAAD9CzEMAAAAJElEQVR42mNgGAX0gP8zA8M/AwPDfwYwEwZkBGYQZgBiAxhRzgAAAgwAAWgD1tAAAAAASUVORK5CYII=");
                        File.WriteAllBytes(imgPath, bytes);
                    }

                    var b = new Button
                    {
                        Width = 190,
                        Height = 240,
                        Margin = new Thickness(30, 10, 30, 10),
                        Background = System.Windows.Media.Brushes.Transparent,
                        BorderBrush = System.Windows.Media.Brushes.Transparent,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = user
                    };
                    var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    var img = new Image
                    {
                        Source = new BitmapImage(new System.Uri(System.IO.Path.GetFullPath(imgPath))),
                        Width = 120,
                        Height = 120,
                        Margin = new Thickness(0, 0, 0, 15)
                    };
                    var tb = new TextBlock
                    {
                        Text = user,
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    };
                    sp.Children.Add(img);
                    sp.Children.Add(tb);
                    b.Content = sp;
                    b.Click += (s, e) =>
                    {
                        _mainWindow.SwitchToDashboard(user, imgPath);
                    };
                    AccountsPanel.Items.Add(b);
                }
            }
        }

        private void AddAccountBtn_Click(object sender, RoutedEventArgs e)
        {
            var inputWin = new Window
            {
                Title = "Add Account",
                Width = 350,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow,
                WindowStyle = WindowStyle.ToolWindow,
                Background = System.Windows.Media.Brushes.White,
                ShowInTaskbar = false
            };
            var tb = new TextBox { Margin = new Thickness(16, 28, 16, 8), FontSize = 22 };
            var okBtn = new Button { Content = "Add", Width = 90, Height = 38, Margin = new Thickness(16, 8, 16, 16), FontSize = 18 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Account Name", FontSize = 16, Margin = new Thickness(16, 10, 16, 0) });
            sp.Children.Add(tb);
            sp.Children.Add(okBtn);
            inputWin.Content = sp;

            okBtn.Click += (s, ev) =>
            {
                string name = tb.Text.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show("Please enter a valid account name (no special characters).");
                    return;
                }
                string accDir = System.IO.Path.Combine(accountsRoot, name);
                if (!Directory.Exists(accDir))
                {
                    Directory.CreateDirectory(accDir);
                    var bytes = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAQAAAD9CzEMAAAAJElEQVR42mNgGAX0gP8zA8M/AwPDfwYwEwZkBGYQZgBiAxhRzgAAAgwAAWgD1tAAAAAASUVORK5CYII=");
                    File.WriteAllBytes(System.IO.Path.Combine(accDir, "profile.png"), bytes);

                    // Create empty installed_apps.json file for this account
                    string appsJsonPath = System.IO.Path.Combine(accDir, "installed_apps.json");
                    File.WriteAllText(appsJsonPath, "[]");
                }
                inputWin.DialogResult = true;
                inputWin.Close();
                RefreshAccounts();
            };
            inputWin.ShowDialog();
        }
    }
}
