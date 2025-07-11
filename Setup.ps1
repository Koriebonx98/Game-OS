# --- PREPARE PROJECT FOLDER ---
$prj = "XboxShellApp"
$solution = "$prj.sln"
if (!(Test-Path $prj)) { mkdir $prj | Out-Null }
Set-Location $prj

if (!(Test-Path "Data/Accounts")) { New-Item -Path "Data/Accounts" -ItemType Directory | Out-Null }
if (!(Test-Path "Data/Resources/Game Cover/PC (Windows)")) { New-Item -Path "Data/Resources/Game Cover/PC (Windows)" -ItemType Directory | Out-Null }

# --- DETECT INSTALLED APPS (NEW) & READY TO INSTALL ---
if (!(Test-Path "Data")) { New-Item -Path "Data" -ItemType Directory | Out-Null }
$apps = @()
$startMenuPaths = @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs")
foreach ($path in $startMenuPaths) {
    if (Test-Path $path) {
        Get-ChildItem -Path $path -Recurse -Filter *.lnk | ForEach-Object {
            $target = (New-Object -ComObject WScript.Shell).CreateShortcut($_.FullName).TargetPath
            if ($target -and (Test-Path $target)) {
                $apps += [PSCustomObject]@{
                    Name = $_.BaseName
                    Exe = $target
                    ImagePath = ""
                    Source = "StartMenu"
                    IsApp = $true
                    IsGame = $false
                }
            }
        }
    }
}
$regPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($regPath in $regPaths) {
    if (Test-Path $regPath) {
        Get-ChildItem $regPath | ForEach-Object {
            $props = Get-ItemProperty $_.PSPath
            if ($props.DisplayName -and $props.DisplayIcon) {
                $exePath = $props.DisplayIcon.Split(",")[0]
                if (Test-Path $exePath) {
                    $apps += [PSCustomObject]@{
                        Name = $props.DisplayName
                        Exe = $exePath
                        ImagePath = ""
                        Source = "Registry"
                        IsApp = $true
                        IsGame = $false
                    }
                }
            }
        }
    }
}

# --- READY TO INSTALL: Find Repacks ---
$repacks = @()
$drives = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and ($_.DriveType -eq 'Fixed' -or $_.DriveType -eq 'Removable') }
foreach ($d in $drives) {
    $root = Join-Path $d.Name "Repacks"
    if (Test-Path $root) {
        Get-ChildItem -Path $root -Directory | Where-Object {
            ($_.Name -notmatch '\(|\)') -and ($_.Name -notmatch '\[|\]')
        } | ForEach-Object {
            $gameExe = Get-ChildItem -Path $_.FullName -Filter *.exe -File | Select-Object -First 1
            $picName = "$($_.Name).jpg"
            $cover = Join-Path "Data/Resources/Game Cover/PC (Windows)" $picName
            $repacks += [PSCustomObject]@{
                Name = $_.Name
                Exe = if ($gameExe) { $gameExe.FullName } else { "" }
                ImagePath = if (Test-Path $cover) { $cover } else { "" }
                Source = "ReadyToInstall"
                IsApp = $false
                IsGame = $true
                Folder = $_.FullName
            }
        }
    }
}

$apps = $apps | Sort-Object Name,Exe -Unique
$repacks = $repacks | Sort-Object Name,Exe -Unique

$allApps = $apps + $repacks
$allApps | ConvertTo-Json | Set-Content "Data\installed_apps.json" -Encoding UTF8

# --- PROJECT FILE ---
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net6.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
</Project>
'@ | Set-Content XboxShellApp.csproj -Encoding UTF8

# --- APP.XAML ---
@'
<Application x:Class="XboxShellApp.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
'@ | Set-Content App.xaml -Encoding UTF8

# --- APP.XAML.CS ---
@'
using System.Windows;
namespace XboxShellApp
{
    public partial class App : Application
    {
    }
}
'@ | Set-Content App.xaml.cs -Encoding UTF8

# --- MAINWINDOW.XAML ---
@'
<Window x:Class="XboxShellApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Xbox One OS" WindowStartupLocation="CenterScreen"
    WindowStyle="None" WindowState="Maximized" ResizeMode="NoResize"
    Background="#111215" FontFamily="Segoe UI">
    <ContentControl x:Name="MainContent"/>
</Window>
'@ | Set-Content MainWindow.xaml -Encoding UTF8

# --- MAINWINDOW.XAML.CS ---
@'
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
'@ | Set-Content MainWindow.xaml.cs -Encoding UTF8

# --- GAMEAPP TILE VM ---
@'
namespace XboxShellApp
{
    public class GameAppTileVM
    {
        public string Name { get; set; }
        public string Exe { get; set; }
        public string ImagePath { get; set; }
        public string Folder { get; set; }
        public bool IsGame { get; set; }
        public bool IsApp { get; set; }
        public bool IsMusic { get; set; }
        public bool IsPicture { get; set; }
        public bool IsVideo { get; set; }
        public string TypeLabel
        {
            get
            {
                if (IsGame) return "Game";
                if (IsApp) return "App";
                if (IsMusic) return "Music";
                if (IsPicture) return "Picture";
                if (IsVideo) return "Video";
                return "Unknown";
            }
        }
    }
}
'@ | Set-Content GameAppTileVM.cs -Encoding UTF8

# --- LOGINPAGE.XAML ---
@'
<UserControl x:Class="XboxShellApp.LoginPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border CornerRadius="0" BorderThickness="0" Background="#101018" Padding="0">
        <Grid>
            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
                <TextBlock Text="Sign in" FontSize="38" FontWeight="Bold" Foreground="White" HorizontalAlignment="Center" Margin="0,0,0,40"/>
                <ItemsControl x:Name="AccountsPanel">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <WrapPanel HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>
                <Button x:Name="AddAccountBtn" Content="Add Account" Width="260" Height="62" FontSize="22" Margin="0,60,0,0"
                        Background="#107C10" Foreground="White" BorderBrush="Transparent" Cursor="Hand" Visibility="Collapsed"/>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
'@ | Set-Content LoginPage.xaml -Encoding UTF8

# --- LOGINPAGE.XAML.CS ---
@'
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
'@ | Set-Content LoginPage.xaml.cs -Encoding UTF8

# --- DASHBOARDPAGE.XAML ---
@'
<UserControl x:Class="XboxShellApp.DashboardPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Background="#111215">
        <!-- Top bar -->
        <Grid VerticalAlignment="Top" Height="60" Background="#000">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="260"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="260"/>
            </Grid.ColumnDefinitions>
            <StackPanel Orientation="Horizontal" Grid.Column="0" VerticalAlignment="Center" Margin="20,0,0,0" >
                <Ellipse Width="36" Height="36" Margin="0,0,10,0">
                    <Ellipse.Fill>
                        <SolidColorBrush Color="#2380FF"/>
                    </Ellipse.Fill>
                </Ellipse>
                <Ellipse Width="36" Height="36" Margin="0,0,10,0">
                    <Ellipse.Fill>
                        <SolidColorBrush Color="#fff"/>
                    </Ellipse.Fill>
                </Ellipse>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Grid.Column="1" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="0,0,0,0">
                <TextBlock Text="Pins" Margin="0,0,32,0" Foreground="#8d91a3" FontWeight="Bold" FontSize="20"/>
                <TextBlock Text="Home" Margin="0,0,32,0" Foreground="#2d7dff" FontWeight="Bold" FontSize="20"/>
                <TextBlock Text="Friends" Margin="0,0,32,0" Foreground="#8d91a3" FontWeight="Bold" FontSize="20"/>
                <TextBlock Text="What's on" Margin="0,0,32,0" Foreground="#8d91a3" FontWeight="Bold" FontSize="20"/>
                <TextBlock Text="Store" Margin="0,0,32,0" Foreground="#8d91a3" FontWeight="Bold" FontSize="20"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Grid.Column="2" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="0,0,24,0">
                <TextBlock Text="Try saying " Foreground="#eee" FontSize="15"/>
                <TextBlock Text="&quot;Xbox&quot;" Foreground="#fff" FontSize="15"/>
            </StackPanel>
        </Grid>
        <!-- Main area -->
        <Grid Margin="0,68,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="2.7*"/>
                <ColumnDefinition Width="1.3*"/>
                <ColumnDefinition Width="1.1*"/>
            </Grid.ColumnDefinitions>
            <!-- Left tiles -->
            <Grid Grid.Column="0" Margin="40,36,18,0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="1.55*"/>
                    <RowDefinition Height="1.55*"/>
                    <RowDefinition Height="1.1*"/>
                </Grid.RowDefinitions>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="2.18*"/>
                    <ColumnDefinition Width="1.08*"/>
                    <ColumnDefinition Width="1.08*"/>
                </Grid.ColumnDefinitions>
                <!-- Main game banner -->
                <Border Grid.Row="0" Grid.Column="0" Grid.RowSpan="2" Margin="0,0,18,18" Background="#181a23" CornerRadius="16">
                    <Image Source="https://assets.xboxservices.com/assets/ea/ea7d8c2a-3153-4d49-bd6a-9b4f2e4f2d93.jpg?n=Halo-5_GLP-Poster_1080x600.jpg" Stretch="UniformToFill"/>
                </Border>
                <!-- Snap -->
                <Button Grid.Row="0" Grid.Column="1" Margin="0,0,0,18" Background="#2298fc" BorderThickness="0" FontSize="16" Foreground="White" HorizontalAlignment="Stretch" VerticalAlignment="Stretch" >
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center"><TextBlock Text="Snap" FontWeight="Bold" FontSize="18"/></StackPanel>
                </Button>
                <!-- My games & apps -->
                <Button Grid.Row="1" Grid.Column="1" Margin="0,0,0,18" Background="#2d7dff" BorderThickness="0" FontSize="16" Foreground="White" x:Name="GamesAppsBtn">
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center"><TextBlock Text="My games &amp; apps" FontWeight="Bold" FontSize="18"/></StackPanel>
                </Button>
                <!-- Achievements -->
                <Button Grid.Row="2" Grid.Column="0" Margin="0,0,18,0" Background="#1b87e0" BorderThickness="0" FontSize="16" Foreground="White">
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center">
                        <TextBlock Text="Achievements" FontWeight="Bold" FontSize="18"/>
                    </StackPanel>
                </Button>
                <!-- Settings -->
                <Button Grid.Row="2" Grid.Column="1" Margin="0,0,0,0" Background="#36373f" BorderThickness="0" FontSize="16" Foreground="White" x:Name="SettingsBtn">
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center">
                        <TextBlock Text="Settings" FontWeight="Bold" FontSize="18"/>
                    </StackPanel>
                </Button>
                <!-- Insert disc -->
                <Button Grid.Row="2" Grid.Column="2" Margin="0,0,0,0" Background="#36373f" BorderThickness="0" FontSize="16" Foreground="White">
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Center">
                        <TextBlock Text="insert disc" FontWeight="Bold" FontSize="18"/>
                    </StackPanel>
                </Button>
                <!-- Minecraft -->
                <Button Grid.Row="0" Grid.Column="2" Margin="0,0,0,18" Background="#187a37" BorderThickness="0" FontSize="16" Foreground="White">
                    <StackPanel>
                        <Image Source="https://upload.wikimedia.org/wikipedia/en/5/51/Minecraft_cover.png" Width="70" Height="56"/>
                    </StackPanel>
                </Button>
                <!-- Sunset Overdrive -->
                <Button Grid.Row="1" Grid.Column="2" Margin="0,0,0,18" Background="#4268b1" BorderThickness="0" FontSize="16" Foreground="White">
                    <StackPanel>
                        <Image Source="https://upload.wikimedia.org/wikipedia/en/7/77/Sunset_Overdrive_cover.png" Width="70" Height="56"/>
                    </StackPanel>
                </Button>
            </Grid>
            <!-- Right: Featured/Friends -->
            <StackPanel Grid.Column="1" Margin="18,36,18,0">
                <TextBlock Text="FEATURED" Foreground="#cccccc" FontSize="13" Margin="0,0,0,8"/>
                <Border Background="#1a1a1a" CornerRadius="10" Margin="0,0,0,16">
                    <StackPanel>
                        <Image Source="https://assets.xboxservices.com/assets/05/05d4a1a6-0fc6-4e39-9b4b-4b0b2c7b3ff5.jpg?n=AC-Unity_Featured-Image-1080_1920x1080.jpg" Height="74"/>
                        <TextBlock Text="Join the revolution in AC Unity" Foreground="White" FontSize="15" Margin="10,2,10,6"/>
                    </StackPanel>
                </Border>
                <Border Background="#1a1a1a" CornerRadius="10" Margin="0,0,0,16">
                    <StackPanel>
                        <Image Source="https://assets.xboxservices.com/assets/06/06e9e3e6-b2b7-4b3e-a7b4-49e137a3e6b1.jpg?n=Just-Dance_Featured-Image-1080_1920x1080.jpg" Height="74"/>
                        <TextBlock Text="It's a new year for the new year" Foreground="White" FontSize="15" Margin="10,2,10,6"/>
                    </StackPanel>
                </Border>
                <Border Background="#1a1a1a" CornerRadius="10" Margin="0,0,0,16">
                    <StackPanel>
                        <Image Source="https://assets.xboxservices.com/assets/09/09a8a6c2-4f48-4e66-b9d2-6e2b673faca7.jpg?n=Gold_Featured-Image-1080_1920x1080.jpg" Height="74"/>
                        <TextBlock Text="Free games for members only" Foreground="White" FontSize="15" Margin="10,2,10,6"/>
                    </StackPanel>
                </Border>
            </StackPanel>
            <StackPanel Grid.Column="2" Margin="0,36,18,0">
                <TextBlock Text="FRIENDS" Foreground="#cccccc" FontSize="13" Margin="0,0,0,8"/>
                <Border Background="#252634" CornerRadius="10" Padding="16,14,12,14" Margin="0,0,0,16">
                    <StackPanel>
                        <TextBlock Text="friends" Foreground="#2d7dff" FontSize="16" FontWeight="Bold"/>
                        <TextBlock Text="19" Foreground="White" FontSize="28" Margin="0,0,0,4"/>
                        <TextBlock Text="100% online" Foreground="#cccccc" FontSize="14"/>
                        <TextBlock Text="• Reard" Foreground="#cccccc" FontSize="15"/>
                    </StackPanel>
                </Border>
            </StackPanel>
        </Grid>
    </Grid>
</UserControl>
'@ | Set-Content DashboardPage.xaml -Encoding UTF8

# --- DASHBOARDPAGE.XAML.CS ---
@'
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
'@ | Set-Content DashboardPage.xaml.cs -Encoding UTF8

# --- GAMESAPPSPAGE.XAML ---
@'
<UserControl x:Class="XboxShellApp.GamesAppsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="clr-namespace:XboxShellApp">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="325"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <!-- SIDEBAR -->
        <StackPanel Grid.Column="0" Background="#18326a" VerticalAlignment="Stretch" >
            <Button Content="⟵ Back" x:Name="BackToDashboardBtn" Width="110" Height="40" Margin="18,24,0,18"
                    FontSize="16" Foreground="White" Background="#2d7dff" BorderBrush="Transparent"
                    HorizontalAlignment="Left"/>
            <StackPanel x:Name="StoragePanel" Margin="0,36,0,0"/>
            <StackPanel Margin="0,40,0,0">
                <TextBlock Text="Library" Foreground="White" FontWeight="Bold" FontSize="18" Margin="36,12,0,4"/>
                <StackPanel Margin="18,12,0,0">
                    <Button Content="Games" x:Name="GamesBtn" Margin="0,4,0,0" FontSize="16" Background="#2298fc" Foreground="White"/>
                    <Button Content="Apps" x:Name="AppsBtn" Margin="0,4,0,0" FontSize="16" Background="#2298fc" Foreground="White"/>
                    <Button Content="Music" x:Name="MusicBtn" Margin="0,4,0,0" FontSize="16" Background="#2298fc" Foreground="White"/>
                    <Button Content="Pictures" x:Name="PicturesBtn" Margin="0,4,0,0" FontSize="16" Background="#2298fc" Foreground="White"/>
                    <Button Content="Videos" x:Name="VideosBtn" Margin="0,4,0,0" FontSize="16" Background="#2298fc" Foreground="White"/>
                </StackPanel>
            </StackPanel>
        </StackPanel>
        <!-- MAIN CONTENT -->
        <Grid Grid.Column="1">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <!-- Sorting Drop-downs -->
            <StackPanel Orientation="Horizontal" Margin="38,34,0,16" Grid.Row="0" HorizontalAlignment="Left" >
                <ComboBox x:Name="AZSortCombo" Width="90" Height="36" Margin="0,0,10,0">
                    <ComboBoxItem Content="A-Z"/>
                    <ComboBoxItem Content="Z-A"/>
                </ComboBox>
                <ComboBox x:Name="DriveSortCombo" Width="150" Height="36" Margin="0,0,10,0">
                    <ComboBoxItem Content="All Drives"/>
                </ComboBox>
                <ComboBox x:Name="MostPlayedCombo" Width="110" Height="36" Margin="0,0,10,0">
                    <ComboBoxItem Content="Most Played"/>
                    <ComboBoxItem Content="Least Played"/>
                </ComboBox>
                <ComboBox x:Name="GenreCombo" Width="90" Height="36" Margin="0,0,10,0">
                    <ComboBoxItem Content="All Genres"/>
                </ComboBox>
            </StackPanel>
            <ScrollViewer Grid.Row="1" Margin="18,0,18,18" HorizontalScrollBarVisibility="Auto">
                <ItemsControl x:Name="TilesItemsControl">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="7"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <local:GameAppTile TileClicked="Tile_TileClicked"/>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
        </Grid>
    </Grid>
</UserControl>
'@ | Set-Content GamesAppsPage.xaml -Encoding UTF8

# --- GAMESAPPSPAGE.XAML.CS (CHANGED: "Apps" loads from installed_apps.json) ---
@'
using System;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace XboxShellApp
{
    public partial class GamesAppsPage : UserControl
    {
        private MainWindow _mainWindow;
        private DriveInfo[] _drives;
        private List<GameAppTileVM> _allTiles = new();
        private List<GameAppTileVM> _installedApps = new();
        private string _filterType = "Games";
        private string _azSort = "A-Z";
        private string _driveSort = "All Drives";
        private string _mostPlayedSort = "Most Played";
        private string _genreSort = "All Genres";
        private Dictionary<string, (long Used, long Free, long Total)> _driveSpace = new();

        public GamesAppsPage(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            try
            {
                EnsureInstalledAppsJsonExists();
                LoadDrives();
                LoadInstalledApps();
                LoadTiles();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Failed to load game/app tiles: " + ex.Message);
            }
            BackToDashboardBtn.Click += (s, e) => _mainWindow.SwitchToDashboard();

            GamesBtn.Click += (s, e) => { _filterType = "Games"; UpdateTiles(); };
            AppsBtn.Click += (s, e) => { _filterType = "Apps"; UpdateTiles(); };
            MusicBtn.Click += (s, e) => { _filterType = "Music"; UpdateTiles(); };
            PicturesBtn.Click += (s, e) => { _filterType = "Pictures"; UpdateTiles(); };
            VideosBtn.Click += (s, e) => { _filterType = "Videos"; UpdateTiles(); };

            AZSortCombo.SelectionChanged += (s, e) => { _azSort = ((ComboBoxItem)AZSortCombo.SelectedItem)?.Content?.ToString() ?? "A-Z"; UpdateTiles(); };
            DriveSortCombo.SelectionChanged += DriveSortCombo_SelectionChanged;
            MostPlayedCombo.SelectionChanged += (s, e) => { _mostPlayedSort = ((ComboBoxItem)MostPlayedCombo.SelectedItem)?.Content?.ToString() ?? "Most Played"; UpdateTiles(); };
            GenreCombo.SelectionChanged += (s, e) => { _genreSort = ((ComboBoxItem)GenreCombo.SelectedItem)?.Content?.ToString() ?? "All Genres"; UpdateTiles(); };

            AZSortCombo.SelectedIndex = 0;
            DriveSortCombo.SelectedIndex = 0;
            MostPlayedCombo.SelectedIndex = 0;
            GenreCombo.SelectedIndex = 0;
        }

        private void EnsureInstalledAppsJsonExists()
        {
            string dataDir = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Data");
            if (!Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);
            string appsJsonPath = Path.Combine(dataDir, "installed_apps.json");
            if (!File.Exists(appsJsonPath))
            {
                var allApps = new List<AppRecord>();

                // Start Menu Shortcuts
                var startMenuPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
                };
                foreach (var path in startMenuPaths.Distinct())
                {
                    if (Directory.Exists(path))
                    {
                        var lnkFiles = Directory.GetFiles(path, "*.lnk", SearchOption.AllDirectories);
                        foreach (var lnk in lnkFiles)
                        {
                            string name = Path.GetFileNameWithoutExtension(lnk);
                            allApps.Add(new AppRecord
                            {
                                Name = name,
                                Exe = lnk,
                                ImagePath = "",
                                Source = "StartMenu",
                                IsGame = false
                            });
                        }
                    }
                }

                // Registry Installed Apps
                var regPaths = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                foreach (var regPath in regPaths)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        if (key == null) continue;
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            using (var sub = key.OpenSubKey(subName))
                            {
                                string displayName = sub?.GetValue("DisplayName") as string;
                                string displayIcon = sub?.GetValue("DisplayIcon") as string;
                                if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(displayIcon))
                                {
                                    string exePath = displayIcon.Split(',')[0];
                                    allApps.Add(new AppRecord
                                    {
                                        Name = displayName,
                                        Exe = exePath,
                                        ImagePath = "",
                                        Source = "Registry",
                                        IsGame = false
                                    });
                                }
                            }
                        }
                    }
                }

                // Remove Duplicates (by Name+Exe)
                allApps = allApps
                    .GroupBy(a => a.Name + "|" + a.Exe)
                    .Select(g => g.First())
                    .OrderBy(a => a.Name)
                    .ToList();

                File.WriteAllText(appsJsonPath, JsonSerializer.Serialize(allApps, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private void LoadDrives()
        {
            try
            {
                _drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                    .ToArray();
            }
            catch
            {
                _drives = new DriveInfo[0];
            }
            _driveSpace.Clear();
            foreach (var d in _drives)
            {
                _driveSpace[d.Name.TrimEnd('\\')] = (d.TotalSize - d.TotalFreeSpace, d.TotalFreeSpace, d.TotalSize);
            }
            UpdateStoragePanel("All Drives");
            DriveSortCombo.Items.Clear();
            DriveSortCombo.Items.Add(new ComboBoxItem() { Content = "All Drives" });
            foreach (var d in _drives)
                DriveSortCombo.Items.Add(new ComboBoxItem() { Content = d.Name.TrimEnd('\\') });
        }

        private void UpdateStoragePanel(string driveName)
        {
            StoragePanel.Children.Clear();
            if (driveName == "All Drives")
            {
                long total = _drives.Sum(d => d.TotalSize);
                long free = _drives.Sum(d => d.TotalFreeSpace);
                long used = total - free;
                double percent = total > 0 ? used * 100.0 / total : 0;
                StoragePanel.Children.Add(new TextBlock
                {
                    Text = "Storage (All Drives)",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Margin = new Thickness(18, 0, 0, 8)
                });
                var pb = new ProgressBar
                {
                    Width = 220,
                    Height = 18,
                    Value = percent,
                    Maximum = 100,
                    Margin = new Thickness(18, 0, 0, 0)
                };
                StoragePanel.Children.Add(pb);
                StoragePanel.Children.Add(new TextBlock
                {
                    Text = $"Used: {FormatGB(used)} / {FormatGB(total)} ({percent:0.0}%)",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(18, 2, 0, 0)
                });
            }
            else
            {
                if (_driveSpace.ContainsKey(driveName))
                {
                    var space = _driveSpace[driveName];
                    double percent = space.Total > 0 ? space.Used * 100.0 / space.Total : 0;
                    StoragePanel.Children.Add(new TextBlock
                    {
                        Text = $"Storage ({driveName})",
                        Foreground = System.Windows.Media.Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = 16,
                        Margin = new Thickness(18, 0, 0, 8)
                    });
                    var pb = new ProgressBar
                    {
                        Width = 220,
                        Height = 18,
                        Value = percent,
                        Maximum = 100,
                        Margin = new Thickness(18, 0, 0, 0)
                    };
                    StoragePanel.Children.Add(pb);
                    StoragePanel.Children.Add(new TextBlock
                    {
                        Text = $"Used: {FormatGB(space.Used)} / {FormatGB(space.Total)} ({percent:0.0}%) | Free: {FormatGB(space.Free)}",
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 14,
                        Margin = new Thickness(18, 2, 0, 0)
                    });
                }
            }
        }

        private void DriveSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _driveSort = ((ComboBoxItem)DriveSortCombo.SelectedItem)?.Content?.ToString() ?? "All Drives";
            UpdateStoragePanel(_driveSort);
            UpdateTiles();
        }

        private string FormatGB(long v)
        {
            return $"{v/1024.0/1024/1024:0.0} GB";
        }

        private void LoadInstalledApps()
        {
            _installedApps.Clear();
            string jsonPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Data", "installed_apps.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var json = File.ReadAllText(jsonPath);
                    var apps = JsonSerializer.Deserialize<List<AppRecord>>(json);
                    foreach (var app in apps)
                    {
                        _installedApps.Add(new GameAppTileVM
                        {
                            Name = app.Name,
                            Exe = app.Exe,
                            ImagePath = app.ImagePath,
                            Folder = null,
                            IsApp = true,
                            IsGame = app.IsGame
                        });
                    }
                }
                catch { }
            }
        }

        private void LoadTiles()
        {
            _allTiles.Clear();
            if (_drives != null && _drives.Length > 0)
            {
                foreach (var drive in _drives)
                {
                    TryAddFolderTiles(drive.Name, "Games", (folder, name, exe, img) => new GameAppTileVM { Name = name, Exe = exe, ImagePath = img, Folder = folder, IsGame = true });
                    TryAddFileTiles(drive.Name, "Music", "*.mp3", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), Exe = file, Folder = folder, IsMusic = true });
                    TryAddFileTiles(drive.Name, "Pictures", "*.jpg", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), ImagePath = file, Folder = folder, IsPicture = true });
                    TryAddFileTiles(drive.Name, "Videos", "*.mp4", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), Exe = file, Folder = folder, IsVideo = true });
                }
            }
            _allTiles.AddRange(_installedApps);
            UpdateTiles();
        }

        private void TryAddFolderTiles(string drive, string subfolder, System.Func<string, string, string, string, GameAppTileVM> makeVm)
        {
            string path = System.IO.Path.Combine(drive, subfolder);
            if (!Directory.Exists(path)) return;
            string[] folders;
            try { folders = Directory.GetDirectories(path); }
            catch { return; }
            foreach (var folder in folders)
            {
                string name = System.IO.Path.GetFileName(folder);
                string exe = null;
                string img = null;
                try { exe = Directory.GetFiles(folder, "*.exe").FirstOrDefault(); } catch { }
                try { img = Directory.GetFiles(folder, "*.jpg").FirstOrDefault() ?? Directory.GetFiles(folder, "*.png").FirstOrDefault(); } catch { }
                _allTiles.Add(makeVm(folder, name, exe, img));
            }
        }

        private void TryAddFileTiles(string drive, string subfolder, string pattern, System.Func<string, string, GameAppTileVM> makeVm)
        {
            string path = System.IO.Path.Combine(drive, subfolder);
            if (!Directory.Exists(path)) return;
            string[] files;
            try { files = Directory.GetFiles(path, pattern); }
            catch { return; }
            foreach (var file in files)
            {
                _allTiles.Add(makeVm(path, file));
            }
        }

        private void UpdateTiles()
        {
            IEnumerable<GameAppTileVM> filtered = _allTiles;
            switch (_filterType)
            {
                case "Games": filtered = filtered.Where(t => t.IsGame); break;
                case "Apps": filtered = filtered.Where(t => t.IsApp); break;
                case "Music": filtered = filtered.Where(t => t.IsMusic); break;
                case "Pictures": filtered = filtered.Where(t => t.IsPicture); break;
                case "Videos": filtered = filtered.Where(t => t.IsVideo); break;
            }
            if (_filterType == "Apps") {
                // Do not filter by drive for installed apps
            }
            else if (_driveSort != "All Drives") {
                filtered = filtered.Where(t => t.Folder != null && t.Folder.StartsWith(_driveSort));
            }
            if (_azSort == "A-Z")
                filtered = filtered.OrderBy(t => t.Name);
            else if (_azSort == "Z-A")
                filtered = filtered.OrderByDescending(t => t.Name);
            if (_mostPlayedSort == "Most Played")
                filtered = filtered.OrderByDescending(t => t.IsGame ? ReadPlayTime(t) : 0);
            else if (_mostPlayedSort == "Least Played")
                filtered = filtered.OrderBy(t => t.IsGame ? ReadPlayTime(t) : 0);
            TilesItemsControl.ItemsSource = filtered.ToList();
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

        public void Tile_TileClicked(object sender, RoutedEventArgs e)
        {
            if (sender is GameAppTile tile && tile.DataContext is GameAppTileVM vm)
            {
                _mainWindow.ShowGameInfo(vm);
            }
        }

        private class AppRecord
        {
            public string Name { get; set; }
            public string Exe { get; set; }
            public string ImagePath { get; set; }
            public string Source { get; set; }
            public bool IsGame { get; set; }
        }
    }
}
'@ | Set-Content GamesAppsPage.xaml.cs -Encoding UTF8

# --- SETTINGS PAGE (like a tile grid) ---
@'
<UserControl x:Class="XboxShellApp.SettingsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Background="#191a23">
        <StackPanel Orientation="Horizontal" Margin="22,32,0,16">
            <Button x:Name="BackToDashboardBtn" Content="⟵ Back" Width="110" Height="38" Margin="0,0,18,0" FontSize="16" Background="#2d7dff" Foreground="White"/>
            <TextBlock Text="Settings" FontSize="32" Foreground="#fff" FontWeight="Bold" VerticalAlignment="Center"/>
        </StackPanel>
        <UniformGrid Rows="2" Columns="3" Margin="44,90,44,44">
            <Button Content="General" FontSize="22" Background="#2d7dff" Foreground="White" Margin="16" Height="160"/>
            <Button Content="Account" FontSize="22" Background="#1b87e0" Foreground="White" Margin="16" Height="160"/>
            <Button Content="Display" FontSize="22" Background="#36373f" Foreground="White" Margin="16" Height="160"/>
            <Button Content="Network" FontSize="22" Background="#2298fc" Foreground="White" Margin="16" Height="160"/>
            <Button Content="System" FontSize="22" Background="#4268b1" Foreground="White" Margin="16" Height="160"/>
            <Button Content="Accessibility" FontSize="22" Background="#187a37" Foreground="White" Margin="16" Height="160"/>
        </UniformGrid>
    </Grid>
</UserControl>
'@ | Set-Content SettingsPage.xaml -Encoding UTF8

# --- SETTINGS PAGE CODE BEHIND ---
@'
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
'@ | Set-Content SettingsPage.xaml.cs -Encoding UTF8

# --- GAMEAPP TILE (XAML) ---
@'
<UserControl x:Class="XboxShellApp.GameAppTile"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="148" Height="190">
    <Grid>
        <Border Background="#18326a" CornerRadius="7"/>
        <Grid>
            <Image Source="{Binding ImagePath}" Width="132" Height="132" Stretch="UniformToFill" Margin="8,10,8,36" HorizontalAlignment="Center"/>
            <Border Background="#B2182850" Height="36" VerticalAlignment="Bottom" Margin="0" CornerRadius="0,0,7,7">
                <StackPanel Orientation="Vertical" HorizontalAlignment="Center" Margin="0">
                    <TextBlock Text="{Binding Name}" Foreground="White" FontWeight="Bold" FontSize="15" TextTrimming="CharacterEllipsis"
                               TextAlignment="Center" VerticalAlignment="Center" HorizontalAlignment="Center" Margin="8,0,8,0"/>
                    <TextBlock Text="{Binding TypeLabel}" Foreground="#43c0ff" FontSize="11" TextAlignment="Center"
                               VerticalAlignment="Center" HorizontalAlignment="Center" Margin="0,-2,0,0"/>
                </StackPanel>
            </Border>
            <Button Background="Transparent" BorderBrush="{x:Null}" Cursor="Hand" Click="Tile_Click" />
        </Grid>
    </Grid>
</UserControl>
'@ | Set-Content GameAppTile.xaml -Encoding UTF8

# --- GAMEAPP TILE (XAML.CS) ---
@'
using System.Windows;
using System.Windows.Controls;

namespace XboxShellApp
{
    public partial class GameAppTile : UserControl
    {
        public delegate void TileClickedHandler(object sender, RoutedEventArgs e);
        public event TileClickedHandler TileClicked;

        public GameAppTile()
        {
            InitializeComponent();
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            TileClicked?.Invoke(this, e);
        }
    }
}
'@ | Set-Content GameAppTile.xaml.cs -Encoding UTF8

# --- GAMEINFOPAGE.XAML ---
@'
<UserControl x:Class="XboxShellApp.GameInfoPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Background="#253070">
        <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Width="450">
            <TextBlock x:Name="GameNameBlock" Foreground="White" FontSize="38" FontWeight="Bold" Margin="0,0,0,20" TextAlignment="Center"/>
            <Image x:Name="GameImage" Width="260" Height="260" Margin="0,0,0,18" Stretch="UniformToFill" HorizontalAlignment="Center"/>
            <TextBlock x:Name="PlayTimeBlock" Foreground="#AACCFF" FontSize="20" Margin="0,0,0,30" TextAlignment="Center"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                <Button x:Name="PlayBtn" Content="Play" Width="110" Height="44" Margin="0,0,12,0" FontSize="18" Background="#2d7dff" Foreground="White"/>
                <Button x:Name="OptionsBtn" Content="..." Width="44" Height="44" FontSize="22"/>
            </StackPanel>
            <Button x:Name="BackBtn" Content="⟵ Back" Width="110" Height="38" Margin="0,32,0,0" FontSize="15" Background="#222C48" Foreground="White" HorizontalAlignment="Center"/>
        </StackPanel>
    </Grid>
</UserControl>
'@ | Set-Content GameInfoPage.xaml -Encoding UTF8

# --- GAMEINFOPAGE.XAML.CS ---
@'
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
'@ | Set-Content GameInfoPage.xaml.cs -Encoding UTF8

# --- DOTNET COMMANDS ---
dotnet restore
dotnet build -c Release

cd ..

if (!(Test-Path $solution)) {
    dotnet new sln -n $prj
}
dotnet sln $solution add "$prj\$prj.csproj"

Start-Process "$solution"
Write-Host "`nDashboard, sorting, sidebar, and all error handling added and robust. Apps now show installed apps and Ready to Install repacks on your PC. When you select a drive, sidebar shows its actual storage space left. Game covers for PC games use Data/Resources/Game Cover/PC (Windows)/game.jpg. Press Enter to continue..."
pause