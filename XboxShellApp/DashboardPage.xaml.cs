using System.Windows;
using System.Windows.Controls;

namespace XboxShellApp
{
    public partial class DashboardPage : UserControl
    {
        private MainWindow _mainWindow;

        public DashboardPage(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            GamesAppsBtn.Click += (s, e) => _mainWindow.SwitchToGamesApps();
            SettingsBtn.Click += (s, e) => _mainWindow.SwitchToSettings();
        }
    }
}
