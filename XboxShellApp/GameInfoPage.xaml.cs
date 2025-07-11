using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;

namespace XboxShellApp
{
    public partial class GameInfoPage : UserControl
    {
        private MainWindow _mainWindow;
        private GameAppTileVM _vm;

        public GameInfoPage(MainWindow mainWindow, GameAppTileVM vm)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            _vm = vm;

            GameNameBlock.Text = vm.Name;
            if (!string.IsNullOrWhiteSpace(vm.ImagePath) && File.Exists(vm.ImagePath))
                GameImage.Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(vm.ImagePath));
            else
                GameImage.Source = null;

            PlayTimeBlock.Text = vm.IsGame
                ? $"Play time: {ReadPlayTime(vm):0.00} hours"
                : vm.TypeLabel;

            PlayBtn.Click += PlayBtn_Click;
            OptionsBtn.Click += (s, e) => MessageBox.Show("Options coming soon!");
            BackBtn.Click += (s, e) => _mainWindow.SwitchToGamesApps();
        }

        private double ReadPlayTime(GameAppTileVM vm)
        {
            double curTime = 0;
            if (vm.Folder == null) return curTime;
            var record = System.IO.Path.Combine(vm.Folder, "playtime.txt");
            if (File.Exists(record))
                double.TryParse(File.ReadAllText(record), out curTime);
            return curTime;
        }

        private void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_vm.Exe))
            {
                if (_vm.IsGame)
                {
                    var record = System.IO.Path.Combine(_vm.Folder ?? "", "playtime.txt");
                    double curTime = 0;
                    if (File.Exists(record))
                        double.TryParse(File.ReadAllText(record), out curTime);
                    curTime += 0.02;
                    File.WriteAllText(record, curTime.ToString("0.00"));
                    PlayTimeBlock.Text = $"Play time: {curTime:0.00} hours";
                    try { Process.Start(new ProcessStartInfo(_vm.Exe) { UseShellExecute = true }); }
                    catch { MessageBox.Show("Couldn't launch: " + _vm.Exe); }
                }
                else
                {
                    try { Process.Start(new ProcessStartInfo(_vm.Exe) { UseShellExecute = true }); }
                    catch { MessageBox.Show("Couldn't launch file: " + _vm.Exe); }
                }
            }
            else
            {
                MessageBox.Show($"No executable found for this {_vm.TypeLabel.ToLower()}.");
            }
        }
    }
}
