using System.Windows;
using System.Windows.Controls;

namespace XboxShellApp
{
    public partial class SettingsPage : UserControl
    {
        private MainWindow _mainWindow;

        public SettingsPage(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            BackToDashboardBtn.Click += (s, e) => _mainWindow.SwitchToDashboard();
        }
    }
}
