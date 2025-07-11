using System.Windows;

namespace XboxShellApp
{
    public partial class MainWindow : Window
    {
        public string Username { get; set; }
        public string ProfileImagePath { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            MainContent.Content = new LoginPage(this);
        }

        public void SwitchToDashboard(string username = null, string profileImagePath = null)
        {
            if (username != null) Username = username;
            if (profileImagePath != null) ProfileImagePath = profileImagePath;
            MainContent.Content = new DashboardPage(this);
        }

        public void SwitchToGamesApps()
        {
            MainContent.Content = new GamesAppsPage(this);
        }

        public void SwitchToSettings()
        {
            MainContent.Content = new SettingsPage(this);
        }

        public void ShowGameInfo(GameAppTileVM vm)
        {
            MainContent.Content = new GameInfoPage(this, vm);
        }

        public void SwitchToLogin()
        {
            Username = null;
            ProfileImagePath = null;
            MainContent.Content = new LoginPage(this);
        }
    }
}
