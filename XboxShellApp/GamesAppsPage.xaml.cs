using System;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XboxShellApp
{
    public partial class GamesAppsPage : UserControl
    {
        private MainWindow _mainWindow;
        private DriveInfo[] _drives;
        private List<GameAppTileVM> _allTiles = new();
        private List<GameAppTileVM> _installedApps = new();
        private Dictionary<string, GameAppTileVM> _repacksByNormalizedName = new();
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
            ReadyToInstallBtn.Click += (s, e) => { _filterType = "Ready to Install"; UpdateTiles(); };
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
            _repacksByNormalizedName.Clear();
            if (_drives != null && _drives.Length > 0)
            {
                foreach (var drive in _drives)
                {
                    TryAddFolderTiles(drive.Name, "Games", (folder, name, exe, img) => new GameAppTileVM { Name = name, Exe = exe, ImagePath = img, Folder = folder, IsGame = true });
                    TryAddRepackTiles(drive.Name);
                    TryAddFileTiles(drive.Name, "Music", "*.mp3", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), Exe = file, Folder = folder, IsMusic = true });
                    TryAddFileTiles(drive.Name, "Pictures", "*.jpg", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), ImagePath = file, Folder = folder, IsPicture = true });
                    TryAddFileTiles(drive.Name, "Videos", "*.mp4", (folder, file) => new GameAppTileVM { Name = System.IO.Path.GetFileNameWithoutExtension(file), Exe = file, Folder = folder, IsVideo = true });
                }
            }
            // Add deduplicated repacks to all tiles
            _allTiles.AddRange(_repacksByNormalizedName.Values);
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

        private string NormalizeName(string name)
        {
            // Remove parentheses and brackets from the name for normalization
            return System.Text.RegularExpressions.Regex.Replace(name, @"[\(\)\[\]]", "").Trim();
        }

        private void TryAddRepackTiles(string drive)
        {
            string repacksPath = System.IO.Path.Combine(drive, "Repacks");
            if (!Directory.Exists(repacksPath)) return;
            string[] folders;
            try { folders = Directory.GetDirectories(repacksPath); }
            catch { return; }
            foreach (var folder in folders)
            {
                string name = System.IO.Path.GetFileName(folder);
                string normalizedName = NormalizeName(name);
                
                // Skip if we already have this repack (deduplicate by normalized name)
                if (_repacksByNormalizedName.ContainsKey(normalizedName.ToLowerInvariant()))
                    continue;

                string exe = null;
                string img = null;
                try { exe = Directory.GetFiles(folder, "*.exe").FirstOrDefault(); } catch { }
                try { img = Directory.GetFiles(folder, "*.jpg").FirstOrDefault() ?? Directory.GetFiles(folder, "*.png").FirstOrDefault(); } catch { }
                
                var repackTile = new GameAppTileVM
                {
                    Name = name,
                    Exe = exe,
                    ImagePath = img,
                    Folder = folder,
                    IsRepack = true
                };
                
                _repacksByNormalizedName[normalizedName.ToLowerInvariant()] = repackTile;
            }
        }

        private void UpdateTiles()
        {
            IEnumerable<GameAppTileVM> filtered = _allTiles;
            switch (_filterType)
            {
                case "Games": filtered = filtered.Where(t => t.IsGame); break;
                case "Ready to Install": filtered = filtered.Where(t => t.IsRepack); break;
                case "Apps": filtered = filtered.Where(t => t.IsApp); break;
                case "Music": filtered = filtered.Where(t => t.IsMusic); break;
                case "Pictures": filtered = filtered.Where(t => t.IsPicture); break;
                case "Videos": filtered = filtered.Where(t => t.IsVideo); break;
            }
            if (_filterType == "Apps") {
                // Do not filter by drive for installed apps
            }
            else if (_filterType == "Ready to Install") {
                // Do not filter by drive for repacks since they're already deduplicated
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
