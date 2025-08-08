using System;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Text.Json;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using System.Threading;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace XboxShellApp
{
    public partial class GameInfoPage : UserControl
    {
        private MainWindow _mainWindow;
        private List<GameAppTileVM> _gameInstances;
        private GameAppTileVM _vm;
        private GameAppTileVM _preferredGame;
        private string _preferredExe;
        private ObservableCollection<ExeItem> _exeItems = new();

        // Playtime tracking
        private DispatcherTimer _playTimer;
        private DateTime _playStart;
        private long _totalPlaySeconds;
        private Process? _runningProcess;
        private string _userName => _mainWindow?.Username ?? "Guest";
        private string _platform => "PC (Windows)";
        private string PlaytimeJsonPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", _userName, "Total.Playytime.json");

        // Persistent settings
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XboxShellApp", "game_settings.json");

        private Dictionary<string, GameExeSettings> _settings;

        // Navigation buttons for keyboard navigation
        private Button[] _navButtons;

        private CancellationTokenSource _ryujinxCts;

        private long _lastLogPosition = 0;

        // Add a delegate to track the "back" action
        private readonly Action _onBack;

        public GameInfoPage(MainWindow mainWindow, GameAppTileVM game, Action onBack = null, bool fromDashboard = false)
        { 
            try
            {
                InitializeComponent();
                _mainWindow = mainWindow;
                _onBack = onBack;

                // --- Platform-specific game instance selection ---
                if (game.IsRomGame)
                {
                    // Always use the passed-in tile for ROM games (Switch, Wii, etc)
                    _vm = game;
                    _preferredGame = game;
                    _gameInstances = new List<GameAppTileVM> { game };
                }
                else if (IsWiiGame(game))
                {
                    _vm = game;
                    _preferredGame = game;
                    _gameInstances = new List<GameAppTileVM> { game };
                }
                else if (IsSwitchGame(game))
                {
                    _vm = game;
                    _preferredGame = game;
                    _gameInstances = new List<GameAppTileVM> { game };
                }
                else if (IsXboxGame(game))
                {
                    _vm = game;
                    _preferredGame = game;
                    _gameInstances = new List<GameAppTileVM> { game };
                }
                else
                {
                    _gameInstances = FindAllGameInstances(game.Name);
                    if (_gameInstances.Count > 0)
                    {
                        _vm = _gameInstances.FirstOrDefault();
                        _preferredGame = _vm;
                    }
                    else
                    {
                        _vm = game;
                        _preferredGame = game;
                        _gameInstances = new List<GameAppTileVM> { game };
                    }
                }

                if (_vm != null && string.IsNullOrEmpty(_vm.SteamAppId) && _vm.IsGame)
                {
                    Task.Run(async () =>
                    {
                        string appId = await GetSteamAppIdForGameAsync(_vm.Name);
                        if (!string.IsNullOrEmpty(appId))
                        {
                            // Only update if not already set (thread-safe)
                            if (string.IsNullOrEmpty(_vm.SteamAppId))
                                _vm.SteamAppId = appId;
                        }
                    });
                }

                LoadSettings();

                var exes = GetAllExes();
                _preferredExe = exes.FirstOrDefault();

                if (_settings.TryGetValue(GetSettingsKey(), out var s))
                {
                    if (!string.IsNullOrWhiteSpace(s.PreferredExe) && File.Exists(s.PreferredExe))
                        _preferredExe = s.PreferredExe;
                    if (!string.IsNullOrWhiteSpace(s.PreferredRom) && File.Exists(s.PreferredRom))
                        _preferredGame.DefaultRom = s.PreferredRom;
                }

                _totalPlaySeconds = LoadTotalPlaySeconds();

                GameNameBlock.Text = _vm?.Name ?? "Unknown";
                UpdatePlayTimeBlock();
                GameDirBlock.Text = $"Directory: {_vm?.Folder ?? "N/A"}";

                SetBannerBackground();

                // --- Play/Install button logic based on folder location ---
                bool inGames = false, inRepacks = false;
                string gamesDir = null, repacksDir = null;

                if (_vm?.Folder != null)
                {
                    var folder = _vm.Folder.Replace('/', '\\');
                    gamesDir = Directory.GetParent(folder)?.Name?.Equals("Games", StringComparison.OrdinalIgnoreCase) == true ? folder : null;
                    repacksDir = Directory.GetParent(folder)?.Name?.Equals("Repacks", StringComparison.OrdinalIgnoreCase) == true ? folder : null;

                    // Check if this game exists in both "Games" and "Repacks" on any drive
                    foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        var gamesPath = Path.Combine(drive.RootDirectory.FullName, "Games", _vm.Name);
                        var repacksPath = Path.Combine(drive.RootDirectory.FullName, "Repacks", _vm.Name);
                        if (Directory.Exists(gamesPath)) inGames = true;
                        if (Directory.Exists(repacksPath)) inRepacks = true;
                    }
                }

                // Button logic
                if (inGames && inRepacks)
                {
                    PlayBtn.Content = "Play";
                }
                else if (inGames)
                {
                    PlayBtn.Content = "Play";
                }
                else if (inRepacks)
                {
                    // If Setup.exe exists in the repack folder, show Install, else Play
                    var setupExe = Directory.GetFiles(_vm.Folder, "Setup.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    PlayBtn.Content = setupExe != null ? "Install" : "Play";
                }
                else
                {
                    PlayBtn.Content = _vm != null && !_vm.IsInstalled ? "Install" : "Play";
                }

                string platform = GetPlatform(_vm);
                string coversDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Resources", "Game Cover", platform, _vm.Name);
                if (Directory.Exists(coversDir))
                {
                    var imageFiles = Directory.GetFiles(coversDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var coverFile = imageFiles.FirstOrDefault(f =>
                        string.Equals(Path.GetFileNameWithoutExtension(f), "cover", StringComparison.OrdinalIgnoreCase));
                    if (coverFile != null && File.Exists(coverFile))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(coverFile, UriKind.RelativeOrAbsolute);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze();
                            MainCoverImage.Source = bmp;
                        }
                        catch { MainCoverImage.Source = null; }
                    }
                    else
                    {
                        MainCoverImage.Source = null;
                    }
                }
                else
                {
                    MainCoverImage.Source = null;
                }

                PlayBtn.Click += PlayBtn_Click;
                OptionsBtn.Click += OptionsBtn_Click;
                BackBtn.Click += BackButton_Click;

                Loaded += GameInfoPage_Loaded;

                _ = LoadAndShowAchievementProgressAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"GameInfoPage error: {ex.Message}");
            }
        }

        private void GameInfoPage_Loaded(object sender, RoutedEventArgs e)
        {
            _navButtons = new[] { PlayBtn, OptionsBtn, BackBtn };
            PlayBtn.Focus();
            HighlightNavButton(PlayBtn);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (Keyboard.FocusedElement is Button btn && _navButtons != null)
            {
                int idx = Array.IndexOf(_navButtons, btn);

                if (e.Key == Key.Right)
                {
                    // Only open Achievements as overlay if OptionsBtn ("...") is focused
                    if (btn == OptionsBtn)
                    {
                        _mainWindow.ShowAchievementsOverlay(_vm);
                        e.Handled = true;
                        return;
                    }
                    // Move right if not on last button
                    if (idx < _navButtons.Length - 1)
                    {
                        int next = idx + 1;
                        _navButtons[next].Focus();
                        HighlightNavButton(_navButtons[next]);
                    }
                    // else do nothing if already on last button
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Left)
                {
                    // Move left if not on first button (BackBtn)
                    if (idx > 0)
                    {
                        int prev = idx - 1;
                        _navButtons[prev].Focus();
                        HighlightNavButton(_navButtons[prev]);
                    }
                    // else do nothing if already on BackBtn
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Down)
                {
                    int next = (idx + 1) % _navButtons.Length;
                    _navButtons[next].Focus();
                    HighlightNavButton(_navButtons[next]);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Up)
                {
                    int prev = (idx - 1 + _navButtons.Length) % _navButtons.Length;
                    _navButtons[prev].Focus();
                    HighlightNavButton(_navButtons[prev]);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    return;
                }
            }
        }
        private void HighlightNavButton(Button selected)
        {
            foreach (var btn in _navButtons)
            {
                NavHelper.SetIsSelected(btn, btn == selected);
            }
        }

        private void SetBannerBackground()
        {
            string platform = GetPlatform(_vm);
            string coversDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Resources", "Game Cover", platform, _vm.Name);
            if (!Directory.Exists(coversDir)) return;
            string[] exts = new[] { ".png", ".jpg", ".jpeg", ".bmp" };
            string bannerPath = exts.Select(ext => Path.Combine(coversDir, "Banner" + ext))
                                    .FirstOrDefault(File.Exists);
            if (bannerPath != null)
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(bannerPath, UriKind.Absolute);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    RootGrid.Background = new ImageBrush(img)
                    {
                        Stretch = Stretch.UniformToFill,
                        Opacity = 0.35
                    };
                }
                catch { }
            }
            else
            {
                RootGrid.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111215"));
            }
        }

        // --- Section: Wii Game Detection ---
        private bool IsWiiGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("dolphin"))
                return true;
            if (game.Folder?.Contains("Wii", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: Switch Game Detection ---
        private bool IsSwitchGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator))
            {
                var exe = Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant();
                if (exe.Contains("yuzu") || exe.Contains("ryujinx"))
                    return true;
            }
            if (game.Folder?.Contains("Switch", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: Xbox Game Detection ---
        private bool IsXboxGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("xemu"))
                return true;
            if (game.Folder?.Contains("Xbox", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: GameCube Game Detection ---
        private bool IsGameCubeGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("dolphin"))
                return true;
            if (game.Folder?.Contains("GameCube", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: 3DS Game Detection ---
        private bool Is3DSGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("lime 3ds"))
                return true;
            if (game.Folder?.Contains("3DS", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: Wii U Game Detection ---
        private bool IsWiiUGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("cemu"))
                return true;
            if (game.Folder?.Contains("Wii U", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: PlayStation 1 Game Detection ---
        private bool IsPS1Game(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("duckstation"))
                return true;
            if (game.Folder?.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) == true ||
                game.Folder?.Contains("PS1", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: PlayStation 2 Game Detection ---
        private bool IsPS2Game(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("pcsx2"))
                return true;
            if (game.Folder?.Contains("PlayStation 2", StringComparison.OrdinalIgnoreCase) == true ||
                game.Folder?.Contains("PS2", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: PSP Game Detection ---
        private bool IsPSPGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (!string.IsNullOrWhiteSpace(game.Emulator) && Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant().Contains("ppsspp"))
                return true;
            if (game.Folder?.Contains("PSP", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }

        // --- Section: PC Game Detection ---
        private bool IsPCGame(GameAppTileVM game)
        {
            if (game == null) return false;
            if (game.Folder?.Contains("Games", StringComparison.OrdinalIgnoreCase) == true ||
                game.Folder?.Contains("PC Games", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            return false;
        }


        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            // Remove all [ ] ( ) { } < > and their contents
            string cleaned = Regex.Replace(name, @"[\[\(\{\<].*?[\]\)\}\>]", "");

            // Remove any remaining brackets/braces
            cleaned = Regex.Replace(cleaned, @"[\[\]\(\)\{\}\<\>]", "");

            // Remove common trademark/copyright symbols
            cleaned = Regex.Replace(cleaned, "[™®©]", "", RegexOptions.IgnoreCase);

            // Remove common repack/group tags (FitGirl, DODI, etc)
            cleaned = Regex.Replace(cleaned, @"\b(FitGirl|DODI|ElAmigos|Xatab|GOG|PROPHET|CODEX|Razor1911|FLT|PLAZA|GoldBerg|EMPRESS|P2P|Repack)\b", "", RegexOptions.IgnoreCase);

            // Remove "v1.0", "v2.3", etc. at the end or in parentheses
            cleaned = Regex.Replace(cleaned, @"(\s+v\d+(\.\d+)*[a-zA-Z0-9\-]*)", "", RegexOptions.IgnoreCase);

            // Remove trailing dashes, colons, or extra punctuation
            cleaned = Regex.Replace(cleaned, @"[\-\:\,\.]+$", "");

            // Replace multiple spaces with a single space
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

            // Replace the first dash that is NOT between two word characters with ": "
            var match = Regex.Match(cleaned, @"(?<!\w)-\s*|\s*-(?!\w)");
            if (match.Success)
            {
                cleaned = cleaned.Substring(0, match.Index) + ": " + cleaned.Substring(match.Index + match.Length);
            }

            // Trim again to remove any leading/trailing spaces
            return cleaned.Trim();
        }
        private List<GameAppTileVM> FindAllGameInstances(string gameName)
        {
            var games = new List<GameAppTileVM>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string normName = NormalizeGameName(gameName);

            // --- Load Steam All.Games.json for AppID mapping ---
            Dictionary<string, string> steamNameToAppId = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string accountsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts");
                string user = _mainWindow?.Username ?? "Default";
                string allGamesJsonPath = Path.Combine(accountsRoot, user, "Lib", "Steam", "All.Games.json");
                if (File.Exists(allGamesJsonPath))
                {
                    var json = File.ReadAllText(allGamesJsonPath);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var game in doc.RootElement.EnumerateArray())
                    {
                        string appId = game.GetProperty("appid").GetInt32().ToString();
                        string name = NormalizeGameName(game.GetProperty("name").GetString());
                        if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                            steamNameToAppId[name] = appId;
                    }
                }
            }
            catch { }

            // Track if we've already added a Steam game for this AppId
            var addedSteamAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                // 1. <Drive>:\Games\$GameName
                var gamesDir = Path.Combine(drive.RootDirectory.FullName, "Games");
                if (Directory.Exists(gamesDir))
                {
                    foreach (var folder in Directory.GetDirectories(gamesDir))
                    {
                        var folderName = Path.GetFileName(folder);
                        var folderNorm = NormalizeGameName(folderName);
                        if (folderNorm.Equals(normName, StringComparison.OrdinalIgnoreCase) && seenFolders.Add(folder))
                        {
                            string appId = steamNameToAppId.TryGetValue(folderNorm, out var id) ? id : null;
                            games.Add(new GameAppTileVM
                            {
                                Name = folderName,
                                Folder = folder,
                                ImagePath = Directory.GetFiles(folder, "*.png").FirstOrDefault(),
                                Exe = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).FirstOrDefault(),
                                IsGame = true,
                                SteamAppId = appId
                            });
                        }
                    }
                }

                // 2. <Drive>:\SteamLibrary\steamapps\common\$GameName
                var steamCommonDir = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common");
                if (Directory.Exists(steamCommonDir))
                {
                    foreach (var folder in Directory.GetDirectories(steamCommonDir))
                    {
                        var folderName = Path.GetFileName(folder);
                        var folderNorm = NormalizeGameName(folderName);
                        if (folderNorm.Equals(normName, StringComparison.OrdinalIgnoreCase) && seenFolders.Add(folder))
                        {
                            // Try to find the AppId from the manifest
                            string appId = null;
                            var steamAppsDir = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps");
                            if (Directory.Exists(steamAppsDir))
                            {
                                foreach (var manifestFile in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                                {
                                    var lines = File.ReadAllLines(manifestFile);    
                                    string installDir = null;
                                    foreach (var line in lines)
                                    {
                                        if (installDir == null && line.TrimStart().StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                                            installDir = ExtractAcfValue(line);
                                        if (installDir != null)
                                            break;
                                    }
                                    if (string.Equals(installDir, folderName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var match = Regex.Match(Path.GetFileName(manifestFile), @"appmanifest_(\d+)\.acf", RegexOptions.IgnoreCase);
                                        if (match.Success)
                                            appId = match.Groups[1].Value;
                                        break;
                                    }
                                }
                            }
                            // Only add if we haven't already added this AppId
                            if (string.IsNullOrEmpty(appId) || addedSteamAppIds.Add(appId))
                            {
                                games.Add(new GameAppTileVM
                                {
                                    Name = folderName,
                                    Folder = folder,
                                    ImagePath = Directory.GetFiles(folder, "*.png").FirstOrDefault(),
                                    Exe = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).FirstOrDefault(),
                                    IsGame = true,
                                    SteamAppId = appId
                                });
                            }
                        }
                    }
                }

                // 3. <Drive>:\EpicGames\$GameName
                var epicDir = Path.Combine(drive.RootDirectory.FullName, "EpicGames");
                if (Directory.Exists(epicDir))
                {
                    foreach (var folder in Directory.GetDirectories(epicDir))
                    {
                        var folderName = Path.GetFileName(folder);
                        var folderNorm = NormalizeGameName(folderName);
                        if (folderNorm.Equals(normName, StringComparison.OrdinalIgnoreCase) && seenFolders.Add(folder))
                        {
                            string appId = steamNameToAppId.TryGetValue(folderNorm, out var id) ? id : null;
                            games.Add(new GameAppTileVM
                            {
                                Name = folderName,
                                Folder = folder,
                                ImagePath = Directory.GetFiles(folder, "*.png").FirstOrDefault(),
                                Exe = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).FirstOrDefault(),
                                IsGame = true,
                                SteamAppId = appId
                            });
                        }
                    }
                }
            }

            // If no installed Steam game found, but you know the AppId (e.g. from a database), you could add a "not installed" Steam entry here if needed.

            return games;
        }

        private static (HashSet<string> ExcludedFolders, HashSet<string> ExcludedExes) LoadExeExclusions()
        {
            var excludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string exclusionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Exclusions.txt");
            if (!File.Exists(exclusionsPath))
                return (excludedFolders, excludedExes);

            var lines = File.ReadAllLines(exclusionsPath);
            bool inFolders = false, inExes = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;
                if (line.Equals("Folders:", StringComparison.OrdinalIgnoreCase))
                {
                    inFolders = true; inExes = false; continue;
                }
                if (line.Equals("Exe's:", StringComparison.OrdinalIgnoreCase) || line.Equals("Exes:", StringComparison.OrdinalIgnoreCase))
                {
                    inFolders = false; inExes = true; continue;
                }
                if (inFolders)
                    excludedFolders.Add(line);
                else if (inExes)
                    excludedExes.Add(line);
            }
            return (excludedFolders, excludedExes);
        }

        private string[] GetAllExes()
        {
            var exes = new List<string>();
            var seenExePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (excludedFolders, excludedExes) = LoadExeExclusions();

            // Add all .exe files for installed games
            foreach (var g in _gameInstances.Where(g => Directory.Exists(g.Folder)))
            {
                foreach (var exe in Directory.GetFiles(g.Folder, "*.exe", SearchOption.AllDirectories))
                {
                    var exeName = Path.GetFileName(exe);
                    var dirName = Path.GetFileName(Path.GetDirectoryName(exe));
                    if (excludedExes.Contains(exeName) || excludedFolders.Contains(dirName))
                        continue;
                    if (seenExePaths.Add(exe))
                        exes.Add(exe);
                }
            }

            // Add Steam launch entries only for PC (Windows) games, not ROMs
            var addedSteamAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _gameInstances)
            {
                // Only add Steam entries if this is a PC (Windows) game and not a ROM
                if (!string.IsNullOrEmpty(g.SteamAppId)
                    && !g.IsRomGame
                    && GetPlatform(g) == "PC (Windows)"
                    && addedSteamAppIds.Add(g.SteamAppId))
                {
                    string steamExe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        "Steam", "Steam.exe"
                    );
                    if (File.Exists(steamExe))
                    {
                        // Add only one steam.exe entry per SteamAppId
                        exes.Add($"{steamExe}::--applaunch::{g.SteamAppId}");
                        exes.Add($"STEAM_LAUNCH::{g.SteamAppId}::{steamExe}");
                    }
                }
            }

            return exes.ToArray();
        }

        private static string ExtractAcfValue(string line)
        {
            var match = Regex.Match(line, "\"[^\"]+\"\\s+\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        public static List<GameAppTileVM> FindAllGames()
        {
            var games = new Dictionary<string, GameAppTileVM>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var gamesDir = Path.Combine(drive.RootDirectory.FullName, "Games");
                if (Directory.Exists(gamesDir))
                {
                    foreach (var dir in Directory.GetDirectories(gamesDir))
                    {
                        var name = Path.GetFileName(dir);
                        if (!games.ContainsKey(name) && Directory.Exists(dir))
                        {
                            string imagePath = null;
                            string exe = null;
                            try
                            {
                                imagePath = Directory.GetFiles(dir, "*.png").FirstOrDefault();
                            }
                            catch (DirectoryNotFoundException) { }
                            catch (IOException) { }
                            try
                            {
                                exe = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                            }
                            catch (DirectoryNotFoundException) { }
                            catch (IOException) { }
                            games[name] = new GameAppTileVM
                            {
                                Name = name,
                                Folder = dir,
                                ImagePath = imagePath,
                                Exe = exe,
                                IsGame = true
                            };
                        }
                    }
                }
            }
            return games.Values.ToList();
        }


        private string GetGameVersion(string folder)
        {
            if (!Directory.Exists(folder)) return "N/A";
            var versionFile = Directory.GetFiles(folder, "version*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (versionFile != null)
            {
                try { return File.ReadAllText(versionFile).Trim(); } catch { }
            }
            return "N/A";
        }

        private void UpdatePlayTimeBlock()
        {
            PlayTimeBlock.Text = _vm.IsGame
                ? $"Play time: {(_totalPlaySeconds / 3600.0):0.00} hours ({_totalPlaySeconds} seconds)"
                : _vm.TypeLabel;
        }

        private long LoadTotalPlaySeconds()
        {
            try
            {
                if (File.Exists(PlaytimeJsonPath))
                {
                    var lines = File.ReadAllLines(PlaytimeJsonPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(',');
                        if (parts.Length == 3 &&
                            parts[0].Trim() == _vm.Name &&
                            parts[1].Trim() == _platform)
                        {
                            if (long.TryParse(parts[2].Trim(), out var seconds))
                                return seconds;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private void SaveTotalPlaySeconds()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlaytimeJsonPath)!);
                var lines = new List<string>();
                bool found = false;
                if (File.Exists(PlaytimeJsonPath))
                {
                    lines = File.ReadAllLines(PlaytimeJsonPath).ToList();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var parts = lines[i].Split(',');
                        if (parts.Length == 3 &&
                            parts[0].Trim() == _vm.Name &&
                            parts[1].Trim() == _platform)
                        {
                            lines[i] = $"{_vm.Name}, {_platform}, {_totalPlaySeconds}";
                            found = true;
                            break;
                        }
                    }
                }
                if (!found)
                {
                    lines.Add($"{_vm.Name}, {_platform}, {_totalPlaySeconds}");
                }
                File.WriteAllLines(PlaytimeJsonPath, lines);
            }
            catch { }
        }

        private async void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            // ROM/Emulator launch logic (unchanged)
            if (_preferredGame != null && _preferredGame.IsRomGame)
            {
                string emulatorPath = _preferredGame.Emulator;
                string romFile = _preferredGame.DefaultRom ?? _preferredGame.Exe;
                string args = _preferredGame.EmulatorArgs?.Replace("$RomFile", romFile) ?? "";

                if (string.IsNullOrWhiteSpace(romFile) || !File.Exists(romFile))
                {
                    MessageBox.Show($"The specified ROM file \"{romFile}\" does not exist", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PlayBtn.Content = "Play";
                    PlayBtn.IsEnabled = true;
                    return;
                }

                // --- Switch-specific logic for Ryujinx ---
                if (Path.GetFileName(emulatorPath).Equals("Ryujinx.exe", StringComparison.OrdinalIgnoreCase))
                {
                    DeleteRyujinxLogs();
                    await RunScriptThenLaunchAsync(emulatorPath, args);
                    StartRyujinxLogMonitor(_vm.Name);
                    return;
                }

                // --- Other emulators ---
                if (File.Exists(emulatorPath))
                {
                    await RunScriptThenLaunchAsync(emulatorPath, args);
                }
                else
                {
                    MessageBox.Show($"Emulator not found: {emulatorPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // --- PC (Windows) game launch logic using preferred exe and arguments ---
            if (!string.IsNullOrWhiteSpace(_preferredExe) && File.Exists(_preferredExe))
            {
                // Handle Steam launch shortcut
                string appId = null;
                if (_preferredExe.StartsWith("STEAM_LAUNCH::"))
                {
                    var parts = _preferredExe.Split(new[] { "::" }, StringSplitOptions.None);
                    appId = parts[1];
                }
                else if (Path.GetFileName(_preferredExe).Equals("Steam.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_vm.SteamAppId))
                {
                    appId = _vm.SteamAppId;
                }

                if (!string.IsNullOrEmpty(appId))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = $"steam://rungameid/{appId}",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to launch Steam game: {ex.Message}");
                    }
                    return;
                }

                // Use saved arguments if present
                var args = LoadExeArguments(_preferredExe);
                await RunScriptThenLaunchAsync(_preferredExe, args);
                return;
            }

            // Fallback: try to launch the game's default exe if available
            if (!string.IsNullOrWhiteSpace(_vm?.Exe) && File.Exists(_vm.Exe))
            {
                await RunScriptThenLaunchAsync(_vm.Exe, "");
                return;
            }


            // --- Install logic ---
            if (PlayBtn.Content?.ToString() == "Install")
            {
                // 1. Always check for Setup.exe in the game folder and run it if found
                if (!string.IsNullOrWhiteSpace(_vm?.Folder))
                {
                    string setupExePath = Path.Combine(_vm.Folder, "Setup.exe");
                    if (File.Exists(setupExePath))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = setupExePath,
                                WorkingDirectory = _vm.Folder,
                                UseShellExecute = true,
                                Verb = "runas" // Ensure elevation prompt if needed
                            });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to run Setup.exe: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        return;
                    }
                }

                // 2. If user has selected Setup.exe as preferred, run it
                if (!string.IsNullOrWhiteSpace(_preferredExe) &&
                    Path.GetFileName(_preferredExe).Equals("Setup.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(_preferredExe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = _preferredExe,
                            WorkingDirectory = Path.GetDirectoryName(_preferredExe),
                            UseShellExecute = false,
                            Verb = "runas" // Ensure elevation prompt if needed
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to run Setup.exe: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }

                // 3. If user has selected a Steam executable, launch Steam
                if (!string.IsNullOrWhiteSpace(_preferredExe) &&
                    (_preferredExe.StartsWith("STEAM_LAUNCH::") ||
                     Path.GetFileName(_preferredExe).Equals("Steam.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    string appId = null;
                    if (_preferredExe.StartsWith("STEAM_LAUNCH::"))
                    {
                        var parts = _preferredExe.Split(new[] { "::" }, StringSplitOptions.None);
                        appId = parts.Length > 1 ? parts[1] : null;
                    }
                    else if (Path.GetFileName(_preferredExe).Equals("Steam.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_vm.SteamAppId))
                    {
                        appId = _vm.SteamAppId;
                    }

                    if (!string.IsNullOrEmpty(appId))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = $"steam://rungameid/{appId}",
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to launch Steam game: {ex.Message}");
                        }
                        return;
                    }
                }

                // 4. Otherwise, handle download page URLs (FitGirl, Steamrip, buzzheavier, etc)
                // Use user preferred URL if set, otherwise fallback to first saved URL
                string url = null;
                if (_settings.TryGetValue(GetSettingsKey(), out var set) && !string.IsNullOrWhiteSpace(set.PreferredUrl))
                    url = set.PreferredUrl;
                else
                    url = LoadDownloadedGameUrls(_vm.Name).FirstOrDefault();

                if (string.IsNullOrWhiteSpace(url))
                {
                    MessageBox.Show("No download page URL saved for this game. Please add it in the Info section.", "Missing URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (url.Contains("fitgirl", StringComparison.OrdinalIgnoreCase))
                {
                    PlayBtn.IsEnabled = false;
                    PlayBtn.Content = "Scraping...";
                    string magnet = await ScrapeMagnetUrlAsync(url);
                    PlayBtn.IsEnabled = true;
                    PlayBtn.Content = "Install";

                    if (!string.IsNullOrWhiteSpace(magnet))
                    {
                        Clipboard.SetText(magnet);
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = magnet,
                                UseShellExecute = true
                            });
                        }
                        catch
                        {
                            // If the system can't open magnet links, just ignore
                        }
                        MessageBox.Show("Magnet link copied to clipboard and opened:\n\n" + magnet, "FitGirl Magnet", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("No magnet link found on the FitGirl page.", "FitGirl Magnet", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }
                if (url.Contains("steamrip", StringComparison.OrdinalIgnoreCase))
                {
                    PlayBtn.IsEnabled = false;
                    PlayBtn.Content = "Scraping...";
                    string buzzheavierUrl = await ScrapeSteamripForBuzzheavierAsync(url);
                    if (!string.IsNullOrWhiteSpace(buzzheavierUrl))
                    {
                        string downloadUrl = await ScrapeBuzzheavierDownloadAsync(buzzheavierUrl);
                        PlayBtn.IsEnabled = true;
                        PlayBtn.Content = "Install";
                        if (!string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            MessageBox.Show("Download page opened in your browser:\n\n" + downloadUrl, "Steamrip Download", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            MessageBox.Show("Could not find a download link on the Buzzheavier page.", "Steamrip Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        PlayBtn.IsEnabled = true;
                        PlayBtn.Content = "Install";
                        MessageBox.Show("Could not find a Buzzheavier link on the Steamrip page.", "Steamrip Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                // ...handle other URLs (Steamrip, buzzheavier, etc) as before...
            }

            MessageBox.Show("No valid executable found to launch this game.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        private void GameProcess_Exited(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_playTimer != null)
                {
                    _playTimer.Stop();
                    _totalPlaySeconds += (long)(DateTime.UtcNow - _playStart).TotalSeconds;
                    SaveTotalPlaySeconds();
                    UpdatePlayTimeBlock();
                    PlayBtn.Content = "Play";
                    PlayBtn.IsEnabled = true;
                }
                _runningProcess = null;
            });
        }

        private void PlayTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = (long)(DateTime.UtcNow - _playStart).TotalSeconds;
            PlayTimeBlock.Text = $"Play time: {((_totalPlaySeconds + elapsed) / 3600.0):0.00} hours ({_totalPlaySeconds + elapsed} seconds)";
        }

        private void ShowExeSelectionOverlay(string[] exeFiles)
        {
            _exeItems.Clear();
            foreach (var f in exeFiles)
            {
                string display;
                if (f.StartsWith("STEAM_LAUNCH::"))
                {
                    var parts = f.Split(new[] { "::" }, StringSplitOptions.None);
                    display = $"Steam (AppID: {parts[1]})";
                }
                else
                {
                    string driveLetter = Path.GetPathRoot(f)?.TrimEnd('\\');
                    string exeName = Path.GetFileName(f);
                    display = $"{driveLetter}: {exeName}";
                }
                _exeItems.Add(new ExeItem { Display = display, FullPath = f });
            }
            ExeListBox.SelectedIndex = 0;
            ExeOverlay.Visibility = Visibility.Visible;
            ExeListBox.Focus();
        }

        private async void ExeLaunchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ExeListBox.SelectedItem is ExeItem selected)
            {
                try
                {
                    _preferredExe = selected.FullPath;
                    SaveSettings();
                    ExeOverlay.Visibility = Visibility.Collapsed;

                    // --- STEAM URL LAUNCH LOGIC ---
                    string appId = null;
                    if (_preferredExe.StartsWith("STEAM_LAUNCH::"))
                    {
                        var parts = _preferredExe.Split(new[] { "::" }, StringSplitOptions.None);
                        appId = parts[1];
                    }
                    else if (Path.GetFileName(_preferredExe).Equals("Steam.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_vm.SteamAppId))
                    {
                        appId = _vm.SteamAppId;
                    }

                    if (!string.IsNullOrEmpty(appId))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = $"steam://rungameid/{appId}",
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to launch Steam game: {ex.Message}");
                        }
                        return;
                    }
                    // --- END STEAM URL LAUNCH LOGIC ---

                    var args = LoadExeArguments(_preferredExe);
                    await RunScriptThenLaunchAsync(_preferredExe, args);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to launch: {ex.Message}");
                }
            }
        }

        private void ExeCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            ExeOverlay.Visibility = Visibility.Collapsed;
        }

        private void ExeListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ExeLaunchBtn_Click(sender, e);
        }

        private void OptionsBtn_Click(object sender, RoutedEventArgs e)
        {
            ShowPropertiesOverlay("Info");
        }

        // --- Properties Overlay Logic ---

        private void ShowPropertiesOverlay(string section)
        {
            PropertiesOverlay.Visibility = Visibility.Visible;
            ShowPropertiesSection(section);
        }

        // Add this method to GameInfoPage

        private void ShowPropertiesSection(string section)
        {
            if (section == "Info")
            {
                var stack = new StackPanel();

                // Install directory
                stack.Children.Add(new TextBlock
                {
                    Text = $"Install Dir: {_preferredGame.Folder}",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                // Version
                string version = GetGameVersion(_preferredGame.Folder);
                string downloadedVersion = LoadDownloadedGameVersion(_preferredGame.Name);
                if (!string.IsNullOrEmpty(downloadedVersion))
                    version += $"   (Downloaded: {downloadedVersion})";
                stack.Children.Add(new TextBlock
                {
                    Text = $"Version: {version}",
                    Foreground = Brushes.White,
                    FontSize = 16
                });

                // Steam AppID (only for PC (Windows) games, not ROMs)
                if (!string.IsNullOrWhiteSpace(_preferredGame.SteamAppId) &&
                    !_preferredGame.IsRomGame &&
                    GetPlatform(_preferredGame) == "PC (Windows)")
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"Steam AppID: {_preferredGame.SteamAppId}",
                        Foreground = Brushes.White,
                        FontSize = 16,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }

                // Only show input if PC game, not ROM, and no SteamAppId
                if (string.IsNullOrWhiteSpace(_preferredGame.SteamAppId) &&
                    !_preferredGame.IsRomGame &&
                    GetPlatform(_preferredGame) == "PC (Windows)")
                {
                    var inputPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    var appIdBox = new TextBox
                    {
                        Width = 120,
                        FontSize = 16,
                        Margin = new Thickness(0, 0, 8, 0),
                        Background = new SolidColorBrush(Color.FromRgb(34, 44, 72)),
                        Foreground = Brushes.White,
                    };
                    var saveBtn = new Button
                    {
                        Content = "Save",
                        FontSize = 16,
                        Background = new SolidColorBrush(Color.FromRgb(45, 125, 255)),
                        Foreground = Brushes.White,
                        Padding = new Thickness(8, 2, 8, 2)
                    };
                    saveBtn.Click += (s, e) =>
                    {
                        var text = appIdBox.Text.Trim();
                        if (!string.IsNullOrEmpty(text) && text.All(char.IsDigit))
                        {
                            _preferredGame.SteamAppId = text;
                            SaveSettings();
                            ShowPropertiesSection("Info"); // Refresh
                        }
                        else
                        {
                            MessageBox.Show("Please enter a valid numeric Steam AppID.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    };
                    inputPanel.Children.Add(appIdBox);
                    inputPanel.Children.Add(saveBtn);
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Enter Steam AppID:",
                        Foreground = Brushes.White,
                        FontSize = 16,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                    stack.Children.Add(inputPanel);
                }

                // --- URL input for FitGirl/other download page ---
                stack.Children.Add(new TextBlock
                {
                    Text = "Game Download Page URL:",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    Margin = new Thickness(0, 12, 0, 0)
                });

                string savedUrl = LoadDownloadedGameUrls(_vm.Name).FirstOrDefault(); if (string.IsNullOrWhiteSpace(savedUrl))
                {                   
                    // With this corrected code:
                    var storeUrls = GetStoreGameUrls(_vm.Name);
                    savedUrl = storeUrls.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(savedUrl))
                    {
                        // Auto-save it for this user/account
                        SaveDownloadedGameInfo(_vm.Name, savedUrl);
                    }
                }
                var urlPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var urlBox = new TextBox
                {
                    Text = savedUrl ?? "",
                    Width = 340,
                    FontSize = 14,
                    Background = new SolidColorBrush(Color.FromRgb(34, 44, 72)),
                    Foreground = Brushes.White
                };
                var urlSaveBtn = new Button
                {
                    Content = "Save URL",
                    FontSize = 14,
                    Background = new SolidColorBrush(Color.FromRgb(45, 125, 255)),
                    Foreground = Brushes.White,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                urlSaveBtn.Click += (s, e) =>
                {
                    var url = urlBox.Text.Trim();
                    if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
                    {
                        SaveDownloadedGameInfo(_vm.Name, url);
                        MessageBox.Show("URL saved.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Please enter a valid URL (http/https).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                urlPanel.Children.Add(urlBox);
                urlPanel.Children.Add(urlSaveBtn);
                stack.Children.Add(urlPanel);

                // Playtime
                stack.Children.Add(new TextBlock
                {
                    Text = $"Total Playtime: {(_totalPlaySeconds / 3600.0):0.00} hours ({_totalPlaySeconds} seconds)",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                // ROM-specific info (unchanged)
                if (_preferredGame.IsRomGame)
                {
                    // ...existing ROM info code...
                }

                PropertiesContent.Content = stack;
            }
            else if (section == "Executables")
            {
                var stack = new StackPanel();

                stack.Children.Add(new TextBlock
                {
                    Text = "Available Executables",
                    Foreground = Brushes.White,
                    FontSize = 18,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                var exes = GetAllExes();
                string preferred = _preferredExe;

                // Use a group name for radio buttons so only one can be selected
                string radioGroup = "ExeSelectGroup";

                for (int i = 0; i < exes.Length; i++)
                {
                    string exePath = exes[i];
                    string exeName = exePath.StartsWith("STEAM_LAUNCH::")
                        ? $"Steam (AppID: {exePath.Split(new[] { "::" }, StringSplitOptions.None)[1]})"
                        : Path.GetFileName(exePath);

                    var exePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                    var radio = new RadioButton
                    {
                        GroupName = radioGroup,
                        IsChecked = exePath == preferred,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    };

                    radio.Checked += (s, e) =>
                    {
                        _preferredExe = exePath;
                        SaveSettings();
                    };

                    var exeLabel = new TextBlock
                    {
                        Text = exeName,
                        Foreground = Brushes.White,
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 180
                    };

                    var argBox = new TextBox
                    {
                        Text = LoadExeArguments(exePath),
                        Width = 180,
                        FontSize = 14,
                        Background = new SolidColorBrush(Color.FromRgb(34, 44, 72)),
                        Foreground = Brushes.White,
                        Margin = new Thickness(8, 0, 0, 0)
                    };

                    argBox.LostFocus += (s, e) =>
                    {
                        SaveExeArguments(exePath, argBox.Text);
                    };

                    exePanel.Children.Add(radio);
                    exePanel.Children.Add(exeLabel);
                    exePanel.Children.Add(argBox);

                    stack.Children.Add(exePanel);
                }

                // Show current preferred exe
                stack.Children.Add(new TextBlock
                {
                    Text = $"Current Preferred: {Path.GetFileName(_preferredExe) ?? "None"}",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                PropertiesContent.Content = stack;
            }
            else if (section == "Roms")

            {
                var stack = new StackPanel();

                stack.Children.Add(new TextBlock
                {
                    Text = "Available ROMs",
                    Foreground = Brushes.White,
                    FontSize = 18,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                // Defensive: ensure RomFiles is not null
                var roms = _preferredGame.RomFiles ?? new List<string>();
                string preferredRom = _preferredGame.DefaultRom;

                string radioGroup = "RomSelectGroup";

                if (roms.Count == 0)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "No ROMs found for this game.",
                        Foreground = Brushes.Gray,
                        FontSize = 14
                    });
                }
                else
                {
                    foreach (var romPath in roms)
                    {
                        var romPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                        var radio = new RadioButton
                        {
                            GroupName = radioGroup,
                            IsChecked = string.Equals(romPath, preferredRom, StringComparison.OrdinalIgnoreCase),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };

                        radio.Checked += (s, e) =>
                        {
                            _preferredGame.DefaultRom = romPath;
                            SaveSettings();
                        };

                        var romLabel = new TextBlock
                        {
                            Text = Path.GetFileName(romPath),
                            Foreground = Brushes.White,
                            FontSize = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = romPath
                        };

                        romPanel.Children.Add(radio);
                        romPanel.Children.Add(romLabel);

                        stack.Children.Add(romPanel);
                    }
                }

                // Show current preferred ROM
                stack.Children.Add(new TextBlock
                {
                    Text = $"Current Preferred ROM: {Path.GetFileName(_preferredGame.DefaultRom) ?? "None"}",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                PropertiesContent.Content = stack;
            }
            else if (section == "Urls")
            {
                var stack = new StackPanel();

                stack.Children.Add(new TextBlock
                {
                    Text = "Game Download Page URLs:",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    Margin = new Thickness(0, 12, 0, 0)
                });

                // Gather all URLs: user + store, deduplicated
                var userUrls = LoadDownloadedGameUrls(_vm.Name);
                var storeUrls = GetStoreGameUrls(_vm.Name);
                var allUrls = userUrls.Concat(storeUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                string preferredUrl = _settings.TryGetValue(GetSettingsKey(), out var s) ? s.PreferredUrl : null;
                string radioGroup = "UrlSelectGroup_Info";

                if (allUrls.Count == 0)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "No URLs found for this game.",
                        Foreground = Brushes.Gray,
                        FontSize = 14
                    });
                }
                else
                {
                    foreach (var url in allUrls)
                    {
                        var urlPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                        var radio = new RadioButton
                        {
                            GroupName = radioGroup,
                            IsChecked = string.Equals(url, preferredUrl, StringComparison.OrdinalIgnoreCase),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        radio.Checked += (s2, e2) =>
                        {
                            if (_settings.TryGetValue(GetSettingsKey(), out var set))
                                set.PreferredUrl = url;
                            else
                                _settings[GetSettingsKey()] = new GameExeSettings { PreferredUrl = url };
                            SaveSettings();
                        };

                        var urlBox = new TextBox
                        {
                            Text = url,
                            Width = 340,
                            FontSize = 14,
                            Background = new SolidColorBrush(Color.FromRgb(34, 44, 72)),
                            Foreground = Brushes.White,
                            Margin = new Thickness(0, 0, 8, 0)
                        };

                        // Only allow editing/saving for user URLs
                        bool isUserUrl = userUrls.Contains(url, StringComparer.OrdinalIgnoreCase);
                        Button urlSaveBtn = null;
                        if (isUserUrl)
                        {
                            urlSaveBtn = new Button
                            {
                                Content = "Save",
                                FontSize = 14,
                                Background = new SolidColorBrush(Color.FromRgb(45, 125, 255)),
                                Foreground = Brushes.White,
                                Padding = new Thickness(8, 2, 8, 2)
                            };
                            urlSaveBtn.Click += (s2, e2) =>
                            {
                                var newUrl = urlBox.Text.Trim();
                                if (!string.IsNullOrEmpty(newUrl) && (newUrl.StartsWith("http://") || newUrl.StartsWith("https://")))
                                {
                                    var updatedUserUrls = userUrls.Select(u => u == url ? newUrl : u).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                    SaveDownloadedGameInfo(_vm.Name, string.Join(",", updatedUserUrls));
                                    // Update preferred if needed
                                    if (string.Equals(url, preferredUrl, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (_settings.TryGetValue(GetSettingsKey(), out var set))
                                            set.PreferredUrl = newUrl;
                                        else
                                            _settings[GetSettingsKey()] = new GameExeSettings { PreferredUrl = newUrl };
                                        SaveSettings();
                                    }
                                    MessageBox.Show("URL updated.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                                    ShowPropertiesSection("Urls");
                                }
                                else
                                {
                                    MessageBox.Show("Please enter a valid URL (http/https).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            };
                        }

                        urlPanel.Children.Add(radio);
                        urlPanel.Children.Add(urlBox);
                        if (urlSaveBtn != null)
                            urlPanel.Children.Add(urlSaveBtn);

                        stack.Children.Add(urlPanel);
                    }
                }

                // Add new URL input (always adds to user URLs)
                var addUrlPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var addUrlBox = new TextBox
                {
                    Width = 340,
                    FontSize = 14,
                    Background = new SolidColorBrush(Color.FromRgb(34, 44, 72)),
                    Foreground = Brushes.White
                };
                var addUrlBtn = new Button
                {
                    Content = "Add URL",
                    FontSize = 14,
                    Background = new SolidColorBrush(Color.FromRgb(45, 125, 255)),
                    Foreground = Brushes.White,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                addUrlBtn.Click += (s2, e2) =>
                {
                    var newUrl = addUrlBox.Text.Trim();
                    if (!string.IsNullOrEmpty(newUrl) && (newUrl.StartsWith("http://") || newUrl.StartsWith("https://")))
                    {
                        var updatedUserUrls = userUrls.Concat(new[] { newUrl }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        SaveDownloadedGameInfo(_vm.Name, string.Join(",", updatedUserUrls));
                        // Optionally set as preferred if none set
                        if (string.IsNullOrEmpty(preferredUrl))
                        {
                            if (_settings.TryGetValue(GetSettingsKey(), out var set))
                                set.PreferredUrl = newUrl;
                            else
                                _settings[GetSettingsKey()] = new GameExeSettings { PreferredUrl = newUrl };
                            SaveSettings();
                        }
                        MessageBox.Show("URL added.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                        ShowPropertiesSection("Urls");
                    }
                    else
                    {
                        MessageBox.Show("Please enter a valid URL (http/https).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                addUrlPanel.Children.Add(addUrlBox);
                addUrlPanel.Children.Add(addUrlBtn);
                stack.Children.Add(addUrlPanel);

                PropertiesContent.Content = stack;
            }
        }




        private void InfoBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Info");
        private void ExecutablesBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Executables");
        private void ScriptsBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Scripts");
        private void RomsBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Roms");
        private void EmulatorBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Emulator");
        private void ModsBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Mods");
        private void UrlsBtn_Click(object sender, RoutedEventArgs e) => ShowPropertiesSection("Urls");
        private void PropertiesCloseBtn_Click(object sender, RoutedEventArgs e) => PropertiesOverlay.Visibility = Visibility.Collapsed;

        // ... (rest of the file remains unchanged)

        // In GameInfoPage, close overlay on Back button
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // This assumes _mainWindow is set correctly
            _mainWindow.CloseGameInfoOverlay();
        }
        // --- Persistent Settings ---

        private string GetSettingsKey()
        {
            return $"{_vm.Name}".ToLowerInvariant();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _settings = JsonSerializer.Deserialize<Dictionary<string, GameExeSettings>>(json) ?? new();
                }
                else
                {
                    _settings = new();
                }
                if (_settings.TryGetValue(GetSettingsKey(), out var s))
                {
                    if (!string.IsNullOrWhiteSpace(s.PreferredExe) && File.Exists(s.PreferredExe))
                        _preferredExe = s.PreferredExe;
                    if (!string.IsNullOrWhiteSpace(s.PreferredRom) && File.Exists(s.PreferredRom))
                        _preferredGame.DefaultRom = s.PreferredRom;
                    // No need to check file for URL
                }
            }
            catch
            {
                _settings = new();
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                _settings[GetSettingsKey()] = new GameExeSettings
                {
                    PreferredExe = _preferredExe,
                    PreferredRom = _preferredGame.DefaultRom,
                    PreferredUrl = _settings.TryGetValue(GetSettingsKey(), out var s) ? s.PreferredUrl : null
                };
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        private string LoadExeArguments(string exePath)
        {
            var argFile = exePath + ".args";
            if (File.Exists(argFile))
                return File.ReadAllText(argFile);
            return string.Empty;
        }

        private void SaveExeArguments(string exePath, string arguments)
        {
            var argFile = exePath + ".args";
            File.WriteAllText(argFile, arguments ?? string.Empty);
        }

        private string LoadScriptFile(string scriptName)
        {
            var path = Path.Combine(_vm.Folder, scriptName);
            if (File.Exists(path))
                return File.ReadAllText(path);
            return string.Empty;
        }

        private void SaveScriptFile(string scriptName, string content)
        {
            var path = Path.Combine(_vm.Folder, scriptName);
            File.WriteAllText(path, content ?? string.Empty);
        }

        private void RunScriptIfExists(string scriptFile)
        {
            if (string.IsNullOrWhiteSpace(scriptFile) || !File.Exists(scriptFile))
                return;

            string ext = Path.GetExtension(scriptFile).ToLowerInvariant();

            try
            {
                if (ext == ".bat" || ext == ".cmd")
                {
                    // Run batch/cmd file non-blocking, with working directory set
                    var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = scriptFile,
                            WorkingDirectory = _vm.Folder,
                            UseShellExecute = true,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        }
                    };
                    proc.Start();
                }
                else
                {
                    // Assume PowerShell script
                    string scriptContent = File.ReadAllText(scriptFile);
                    if (string.IsNullOrWhiteSpace(scriptContent))
                        return;

                    string debugLog = $"\"Script started at $(Get-Date)\" | Out-File -FilePath \"$PSScriptRoot\\script_debug.txt\" -Append\n";
                    scriptContent = debugLog + scriptContent + "\n\"Script ended at $(Get-Date)\" | Out-File -FilePath \"$PSScriptRoot\\script_debug.txt\" -Append\n";

                    string tempPs1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ps1");
                    File.WriteAllText(tempPs1, scriptContent);

                    var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                            UseShellExecute = true,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = _vm.Folder
                        }
                    };
                    proc.Start();
                }
            }
            catch { }
        }

        private string GetPlatform(GameAppTileVM game)
        {
            if (game == null) return "PC (Windows)";

            if (!string.IsNullOrWhiteSpace(game.Emulator))
            {
                var exe = Path.GetFileNameWithoutExtension(game.Emulator).ToLowerInvariant();
                if (exe.Contains("dolphin")) return "Nintendo - GameCube"; // Prefer GameCube for Dolphin, fallback below
                if (exe.Contains("yuzu") || exe.Contains("ryujinx")) return "Nintendo - Switch";
                if (exe.Contains("xemu")) return "Microsoft - Xbox";
                if (exe.Contains("duckstation")) return "Sony - PlayStation";
                if (exe.Contains("pcsx2")) return "Sony - PlayStation 2";
                if (exe.Contains("rpcs3")) return "Sony - PlayStation 3";
                if (exe.Contains("ppsspp")) return "Sony - PlayStation Portable";
                if (exe.Contains("cemu")) return "Nintendo - Wii U";
                if (exe.Contains("lime 3ds")) return "Nintendo - 3DS";
                if (exe.Contains("citra")) return "Nintendo - 3DS";
                if (exe.Contains("melon")) return "Nintendo - DS";
                if (exe.Contains("desmume")) return "Nintendo - DS";
                if (exe.Contains("vita3k")) return "Sony - PlayStation Vita";
                if (exe.Contains("xenia")) return "Microsoft - Xbox 360";
                if (exe.Contains("rpcs3")) return "Sony - PlayStation 3";
                if (exe.Contains("cxbx")) return "Microsoft - Xbox";
                if (exe.Contains("mame")) return "Arcade";
                if (exe.Contains("snes9x")) return "Nintendo - SNES";
                if (exe.Contains("zsnes")) return "Nintendo - SNES";
                if (exe.Contains("fceux")) return "Nintendo - NES";
                if (exe.Contains("nestopia")) return "Nintendo - NES";
                if (exe.Contains("mgba")) return "Nintendo - GameBoy Advance";
                if (exe.Contains("visualboyadvance")) return "Nintendo - GameBoy Advance";
                if (exe.Contains("melon")) return "Nintendo - DS";
                if (exe.Contains("mednafen")) return "Sony - PlayStation";
                if (exe.Contains("pcsx")) return "Sony - PlayStation";
                if (exe.Contains("openbor")) return "OpenBOR";
                if (exe.Contains("retroarch")) return "RetroArch";
            }

            if (game.Folder != null)
            {
                var folder = game.Folder.Replace('/', '\\');
                if (folder.Contains(@"\Repacks\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\Games\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PC Games\", StringComparison.OrdinalIgnoreCase))
                    return "PC (Windows)";
                if (folder.Contains(@"\Wii U\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Wii U";
                if (folder.Contains(@"\Wii\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - WII";
                if (folder.Contains(@"\Switch\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Switch";
                if (folder.Contains(@"\Xbox 360\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox 360";
                if (folder.Contains(@"\Xbox One\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox One";
                if (folder.Contains(@"\Xbox Series\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Series";
                if (folder.Contains(@"\Xbox Live Arcade\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Arcade";
                if (folder.Contains(@"\Xbox Live Indie\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Indie";
                if (folder.Contains(@"\Xbox\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox";
                if (folder.Contains(@"\PlayStation 3\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PS3\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 3";
                if (folder.Contains(@"\PlayStation 4\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PS4\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 4";
                if (folder.Contains(@"\PlayStation 5\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PS5\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 5";
                if (folder.Contains(@"\PlayStation Vita\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PSV\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Vita";
                if (folder.Contains(@"\PlayStation Portable\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PSP\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Portable";
                if (folder.Contains(@"\PlayStation 2\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PS2\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 2";
                if (folder.Contains(@"\PlayStation\", StringComparison.OrdinalIgnoreCase) ||
                    folder.Contains(@"\PS1\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation";
                if (folder.Contains(@"\3DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - 3DS";
                if (folder.Contains(@"\DSi\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DSi";
                if (folder.Contains(@"\DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DS";
                if (folder.Contains(@"\GameCube\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameCube";
                if (folder.Contains(@"\SNES\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - SNES";
                if (folder.Contains(@"\Snes\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - SNES";
                if (folder.Contains(@"\NES\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - NES";
                if (folder.Contains(@"\GameBoy Advance\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Advance";
                if (folder.Contains(@"\GameBoy Color\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Color";
                if (folder.Contains(@"\GameBoy\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy";
                if (folder.Contains(@"\Arcade\", StringComparison.OrdinalIgnoreCase)) return "Arcade";
                if (folder.Contains(@"\OpenBOR\", StringComparison.OrdinalIgnoreCase)) return "OpenBOR";
                if (folder.Contains(@"\RetroArch\", StringComparison.OrdinalIgnoreCase)) return "RetroArch";
            }
            return "PC (Windows)";
        }

        private class GameExeSettings
        {
            public string PreferredExe { get; set; }
            public string PreferredRom { get; set; }
            public string PreferredUrl { get; set; }
        }

        private class ExeItem
        {
            public string Display { get; set; }
            public string FullPath { get; set; }
        }

        // Helper class for deserialization
        private class StoreGameEntry
        {
            public string Name { get; set; }
            public object Url { get; set; } // Can be string or string[]
        }

        private async Task RunScriptThenLaunchAsync(string exePath, string arguments)
        {
            string beforeScriptPath = Path.Combine(_vm.Folder, "before.script");
            bool scriptOk = await RunScriptAndWaitAsync(beforeScriptPath);

            if (!scriptOk)
            {
                MessageBox.Show("Script failed or was cancelled. Game launch cancelled.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                PlayBtn.Content = "Playing...";
                PlayBtn.IsEnabled = false;
                _playStart = DateTime.UtcNow;
                _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playTimer.Tick += PlayTimer_Tick;
                _playTimer.Start();

                _runningProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        Arguments = arguments,
                        UseShellExecute = false, // <-- CRITICAL: use false to ensure arguments are passed
                        CreateNoWindow = false
                    },
                    EnableRaisingEvents = true
                };
                _runningProcess.Exited += GameProcess_Exited;
                _runningProcess.Start();
            }
            catch
            {
                PlayBtn.Content = "Play";
                PlayBtn.IsEnabled = true;
                MessageBox.Show("Failed to launch the game/app.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<bool> RunScriptAndWaitAsync(string scriptFile)
        {
            if (string.IsNullOrWhiteSpace(scriptFile) || !File.Exists(scriptFile))
                return true; // No script, just continue

            string ext = Path.GetExtension(scriptFile).ToLowerInvariant();

            try
            {
                ProcessStartInfo psi;
                if (ext == ".bat" || ext == ".cmd")
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = scriptFile,
                        WorkingDirectory = _vm.Folder,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                }
                else
                {
                    string scriptContent = File.ReadAllText(scriptFile);
                    if (string.IsNullOrWhiteSpace(scriptContent))
                        return true;

                    string tempPs1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ps1");
                    File.WriteAllText(tempPs1, scriptContent);

                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                        WorkingDirectory = _vm.Folder,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                }

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                await proc.WaitForExitAsync();
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Script error: {ex.Message}", "Script Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void DeleteRyujinxLogs()
        {
            string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Emulators", "Ryujinx", "portable", "Logs");
            if (Directory.Exists(logsDir))
            {
                var logFiles = Directory.GetFiles(logsDir, "*.log");
                if (logFiles.Length == 0)
                {
                    Debug.WriteLine("[RyujinxMonitor] No logs found to delete in Ryujinx Logs directory.");
                }
                else
                {
                    foreach (var file in logFiles)
                    {
                        try
                        {
                            File.Delete(file);
                            Debug.WriteLine($"[RyujinxMonitor] Deleted log: {file}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[RyujinxMonitor] Failed to delete log {file}: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                Debug.WriteLine($"[RyujinxMonitor] Logs directory does not exist: {logsDir}");
            }
        }

        private List<(string Name, string Criteria)> LoadAchievementCriteria(string gameName)
        {
            // Path to the new JSON achievement file
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data", "Accounts", _userName, "Achivements", "Nintendo - Switch", gameName, $"{gameName}.json"
            );
            var achievements = new List<(string, string)>();
            if (!File.Exists(path))
                return achievements;

            try
            {
                var json = File.ReadAllText(path);
                var root = JsonNode.Parse(json);
                var items = root?["Items"]?.AsArray() ?? root?["achievements"]?.AsArray();
                if (items == null)
                    return achievements;

                foreach (var item in items)
                {
                    // Use "desc" as criteria if "Criteria" is missing
                    var name = item?["Name"]?.ToString() ?? item?["Title"]?.ToString() ?? item?["name"]?.ToString();
                    var criteria = item?["Criteria"]?.ToString() ?? item?["desc"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(criteria))
                    {
                        achievements.Add((name, criteria));
                    }
                }
            }
            catch
            {
                // Ignore parse errors, return empty list
            }
            return achievements;
        }
        private Dictionary<string, string> LoadLogTranslations(string gameName)
        {
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data", "Accounts", _userName, "Achivements", "Nintendo - Switch", gameName, $"{gameName}.json"
            );
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return dict;

            try
            {
                var json = File.ReadAllText(path);
                var root = JsonNode.Parse(json);

                // Try to get a "Translations" object or array
                var translationsNode = root?["Translations"];
                if (translationsNode is JsonObject obj)
                {
                    foreach (var kvp in obj)
                    {
                        if (kvp.Value != null)
                            dict[kvp.Key] = kvp.Value.ToString();
                    }
                }
                else if (translationsNode is JsonArray arr)
                {
                    // If array, expect objects with "Key" and "Value"
                    foreach (var item in arr)
                    {
                        var key = item?["Key"]?.ToString();
                        var value = item?["Value"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(key) && value != null)
                            dict[key] = value;
                    }
                }
            }
            catch
            {
                // Ignore parse errors, return empty dict
            }
            return dict;
        }
        private void StartRyujinxLogMonitor(string gameName)
        {
            _ryujinxCts?.Cancel();
            _ryujinxCts = new CancellationTokenSource();
            var token = _ryujinxCts.Token;
            Task.Run(async () =>
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Emulators", "Ruyjinx", "portable", "logs");
                string logFile = null;
                for (int i = 0; i < 50 && logFile == null && !token.IsCancellationRequested; i++)
                {
                    logFile = Directory.GetFiles(logsDir, "Ryujinx_*.log")
                        .OrderByDescending(f => File.GetCreationTimeUtc(f))
                        .FirstOrDefault();
                    if (logFile == null) await Task.Delay(100, token);
                }
                if (logFile == null)
                {
                    Debug.WriteLine("[RyujinxMonitor] No new Ryujinx log file found after waiting.");
                    return;
                }

                var achievements = LoadAchievementCriteria(gameName);
                var translations = LoadLogTranslations(gameName);

                string statsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", _userName, "Achivements", "Nintendo - Switch", gameName);
                string statsPath = Path.Combine(statsDir, "stats.json");
                string achPath = Path.Combine(statsDir, $"{gameName}.json");

                var unlocked = new HashSet<string>();
                if (File.Exists(achPath))
                {
                    try
                    {
                        var achJson = JsonNode.Parse(File.ReadAllText(achPath));
                        foreach (var item in achJson?["Items"]?.AsArray() ?? new JsonArray())
                        {
                            var name = item?["Name"]?.ToString() ?? item?["Title"]?.ToString();
                            if (!string.IsNullOrEmpty(name) && item?["DateUnlocked"]?.ToString() != "0001-01-01T00:00:00")
                                unlocked.Add(name);
                        }
                    }
                    catch { }
                }

                JsonObject stats = null;
                if (File.Exists(statsPath))
                {
                    try
                    {
                        stats = JsonNode.Parse(File.ReadAllText(statsPath)) as JsonObject ?? new JsonObject();
                    }
                    catch
                    {
                        stats = new JsonObject();
                    }
                }
                else
                {
                    stats = new JsonObject();
                }

                long lastPosition = 0;
                while (!token.IsCancellationRequested)
                {
                    if (!File.Exists(logFile))
                    {
                        await Task.Delay(500, token);
                        continue;
                    }

                    try
                    {
                        using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(lastPosition, SeekOrigin.Begin);
                            using (var sr = new StreamReader(fs))
                            {
                                string line;
                                while ((line = await sr.ReadLineAsync()) != null)
                                {
                                    // Only process lines for Mario Kart that contain both "Room: match" and "FinishReason": "Finish"
                                    if (gameName.Equals("Mario Kart 8 Deluxe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!(line.Contains("Room: match") && line.Contains("\"FinishReason\": \"Finish\"")))
                                            continue;
                                    }

                                    var translatedLine = TranslateLogLine(line, translations);

                                    if (translatedLine.Contains("Room: match") || translatedLine.Contains("Report: {"))
                                    {
                                        stats["LastLogLine"] = translatedLine;
                                        stats["LastLogTime"] = DateTime.UtcNow.ToString("O");
                                        File.WriteAllText(statsPath, stats.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                                    }

                                    // --- Enhanced Achievement detection with Rank support ---
                                    var reportMatch = Regex.Match(translatedLine, @"Report:\s*({.*})");
                                    int? rank = null;
                                    string course = null;
                                    if (reportMatch.Success)
                                    {
                                        try
                                        {
                                            var reportJson = JsonNode.Parse(reportMatch.Groups[1].Value);
                                            rank = reportJson?["Rank"]?.GetValue<int>();
                                            course = reportJson?["Course"]?.ToString();
                                        }
                                        catch { }
                                    }

                                    foreach (var (achName, achCriteria) in achievements)
                                    {
                                        if (unlocked.Contains(achName)) continue;

                                        bool needsFirst = achCriteria.Contains("1st", StringComparison.OrdinalIgnoreCase);
                                        bool needsSecond = achCriteria.Contains("2nd", StringComparison.OrdinalIgnoreCase);
                                        bool needsThird = achCriteria.Contains("3rd", StringComparison.OrdinalIgnoreCase);

                                        bool courseMatch = true;
                                        if (!string.IsNullOrEmpty(course))
                                        {
                                            var courseWords = achCriteria.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                            courseMatch = courseWords.Any(w => course.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 || achCriteria.IndexOf(course, StringComparison.OrdinalIgnoreCase) >= 0);
                                        }

                                        if (rank.HasValue && courseMatch)
                                        {
                                            if (needsFirst && rank.Value == 1)
                                            {
                                                unlocked.Add(achName);
                                                UpdateSwitchAchievementJson(gameName, achName);
                                                continue;
                                            }
                                            if (needsSecond && rank.Value == 2)
                                            {
                                                unlocked.Add(achName);
                                                UpdateSwitchAchievementJson(gameName, achName);
                                                continue;
                                            }
                                            if (needsThird && rank.Value == 3)
                                            {
                                                unlocked.Add(achName);
                                                UpdateSwitchAchievementJson(gameName, achName);
                                                continue;
                                            }
                                        }

                                        bool allMatch = achCriteria.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                            .All(w => translatedLine.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                                        if (allMatch)
                                        {
                                            unlocked.Add(achName);
                                            UpdateSwitchAchievementJson(gameName, achName);
                                        }
                                    }
                                }
                                lastPosition = fs.Position;
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"[RyujinxMonitor] Log read error: {ex.Message}");
                    }

                    await Task.Delay(500, token);
                }
            }, token);
        }
        private void UpdateSwitchAchievementJson(string gameName, string achievementName)
        {
            string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", _userName, "Achivements", "Nintendo - Switch", gameName);
            string achPath = Path.Combine(baseDir, $"{gameName}.json");
            string statsPath = Path.Combine(baseDir, "stats.json");

            if (!File.Exists(achPath)) return;

            var achJson = JsonNode.Parse(File.ReadAllText(achPath));
            bool changed = false;
            foreach (var item in achJson?["Items"]?.AsArray() ?? new JsonArray())
            {
                // Some JSONs use "Name", some use "Title"
                var name = item?["Name"]?.ToString() ?? item?["Title"]?.ToString();
                if (name == achievementName)
                {
                    item["DateUnlocked"] = DateTime.UtcNow.ToString("O");
                    changed = true;
                }
            }
            if (changed)
                File.WriteAllText(achPath, achJson!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // --- Update stats.json ---
            JsonObject stats;
            if (File.Exists(statsPath))
            {
                try
                {
                    stats = JsonNode.Parse(File.ReadAllText(statsPath)) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    stats = new JsonObject();
                }
            }
            else
            {
                stats = new JsonObject();
            }

            // Increment unlocked count and log last unlocked achievement
            int unlockedCount = 0;
            foreach (var item in achJson?["Items"]?.AsArray() ?? new JsonArray())
            {
                var dateUnlocked = item?["DateUnlocked"]?.ToString();
                if (!string.IsNullOrEmpty(dateUnlocked) && dateUnlocked != "0001-01-01T00:00:00")
                    unlockedCount++;
            }
            stats["UnlockedCount"] = unlockedCount;
            stats["LastUnlocked"] = achievementName;
            stats["LastUnlockedTime"] = DateTime.UtcNow.ToString("O");
            File.WriteAllText(statsPath, stats.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // --- Update the progress bar live ---
            Dispatcher.Invoke(async () => await LoadAndShowAchievementProgressAsync());
        }
        private string TranslateLogLine(string line, Dictionary<string, string> translations)
        {
            if (translations == null || translations.Count == 0 || string.IsNullOrEmpty(line))
                return line;

            // Build a single regex pattern for all keys, sorted by length descending to avoid partial matches
            var keys = translations.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(translations[k]))
                .OrderByDescending(k => k.Length)
                .Select(Regex.Escape)
                .ToArray();

            if (keys.Length == 0)
                return line;

            var pattern = $@"\b({string.Join("|", keys)})\b";
            return Regex.Replace(line, pattern, m => translations[m.Value], RegexOptions.IgnoreCase);
        }

        private async Task<string> GetSteamAppIdForGameAsync(string gameName)
        {
            // 1. Try local Steam cache (AllSteamGames.json)
            string accountsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts");
            string user = _mainWindow?.Username ?? "Default";
            string steamDir = Path.Combine(accountsRoot, user, "Lib", "Steam");
            string allSteamGamesPath = Path.Combine(steamDir, "AllSteamGames.json");
            string normName = NormalizeGameName(gameName);

            // Also try to clean repack names for matching
            string repackNormName = NormalizeGameName(gameName);
            if (repackNormName.StartsWith("repack", StringComparison.OrdinalIgnoreCase))
            {
                repackNormName = Regex.Replace(repackNormName, @"^repack[\s\-:]*", "", RegexOptions.IgnoreCase).Trim();
            }
            repackNormName = Regex.Replace(repackNormName, @"\b(FitGirl|DODI|ElAmigos|Xatab|GOG|PROPHET|CODEX|Razor1911|FLT|PLAZA|GoldBerg|EMPRESS|P2P|Repack)\b", "", RegexOptions.IgnoreCase).Trim();
            repackNormName = Regex.Replace(repackNormName, @"\s{2,}", " ").Trim();

            if (File.Exists(allSteamGamesPath))
            {
                try
                {
                    var json = File.ReadAllText(allSteamGamesPath);
                    using var doc = JsonDocument.Parse(json);
                    var apps = doc.RootElement.GetProperty("applist").GetProperty("apps");
                    foreach (var app in apps.EnumerateArray())
                    {
                        string appid = app.GetProperty("appid").GetInt32().ToString();
                        string name = app.GetProperty("name").GetString();
                        string normAppName = NormalizeGameName(name);
                        if (!string.IsNullOrWhiteSpace(name) &&
                            (normAppName.Equals(normName, StringComparison.OrdinalIgnoreCase) ||
                             normAppName.Equals(repackNormName, StringComparison.OrdinalIgnoreCase)))
                        {
                            return appid;
                        }
                    }
                }
                catch { }
            }

            // 2. Try all common PC game library folders for a folder name match and fetch Steam AppID for that folder name
            var pcLibFolders = new[]
            {
        "Games", "Repacks", "PC Games", "Epic Games", "GOG Games", "Amazon Games", "Battle.net", "Bethesda",
        "EA Games", "Electronic Arts", "EA Desktop", "Humble", "itch.io", "Legacy Games", "Origin", "Rockstar", "Ubisoft", "Xbox", "Microsoft Games"
    };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                foreach (var lib in pcLibFolders)
                {
                    var libDir = Path.Combine(drive.RootDirectory.FullName, lib);
                    if (Directory.Exists(libDir))
                    {
                        foreach (var folder in Directory.GetDirectories(libDir))
                        {
                            var folderName = Path.GetFileName(folder);
                            var folderNorm = NormalizeGameName(folderName);
                            if (folderNorm.Equals(normName, StringComparison.OrdinalIgnoreCase) ||
                                folderNorm.Equals(repackNormName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Try Steam API for the folder name (may be a better match than display name)
                                string appId = await GetSteamAppIdFromApiAsync(folderName);
                                if (!string.IsNullOrEmpty(appId))
                                    return appId;
                            }
                        }
                    }
                }
            }

            // 3. If not found, try Steam store search API (public, no key needed) for the original gameName
            return await GetSteamAppIdFromApiAsync(gameName);
        }

        // Helper: Query Steam store API for a given name
        private async Task<string> GetSteamAppIdFromApiAsync(string name)
        {
            string normName = NormalizeGameName(name);
            string repackNormName = normName;
            if (repackNormName.StartsWith("repack", StringComparison.OrdinalIgnoreCase))
            {
                repackNormName = Regex.Replace(repackNormName, @"^repack[\s\-:]*", "", RegexOptions.IgnoreCase).Trim();
            }
            repackNormName = Regex.Replace(repackNormName, @"\b(FitGirl|DODI|ElAmigos|Xatab|GOG|PROPHET|CODEX|Razor1911|FLT|PLAZA|GoldBerg|EMPRESS|P2P|Repack)\b", "", RegexOptions.IgnoreCase).Trim();
            repackNormName = Regex.Replace(repackNormName, @"\s{2,}", " ").Trim();

            try
            {
                using var http = new HttpClient();
                string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&cc=us&l=en";
                var resp = await http.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string itemName = item.GetProperty("name").GetString();
                        string appid = item.GetProperty("id").GetInt32().ToString();
                        string normItemName = NormalizeGameName(itemName);
                        if (!string.IsNullOrWhiteSpace(itemName) &&
                            (normItemName.Equals(normName, StringComparison.OrdinalIgnoreCase) ||
                             normItemName.Equals(repackNormName, StringComparison.OrdinalIgnoreCase)))
                        {
                            return appid;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
        public void UpdateAchievementProgress(int unlocked, int total)
        {
            // Always show the panel
            AchievementProgressPanel.Visibility = Visibility.Visible;

            double percent = (total > 0) ? 100.0 * unlocked / total : 0.0;
            AchievementProgressText.Text = $"Achievements: {unlocked} / {total} ({(int)percent}%)";
            AchievementProgressBar.Value = percent;
        }

        private async Task LoadAndShowAchievementProgressAsync()
        {
            string accountsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts");
            string currentUser = _mainWindow?.Username ?? "Default";
            string platform = GetPlatform(_vm);
            string cacheDir = Path.Combine(accountsRoot, currentUser, "Achievements", platform, _vm.Name);
            string cacheFile = Path.Combine(cacheDir, $"{_vm.Name}.json");

            List<AchievementVM> achievements = new();

            bool isSteamGame = !string.IsNullOrWhiteSpace(_vm.SteamAppId) && platform == "PC (Windows)";
            bool isRom = _vm.IsRomGame;

            // For ROMs: ensure Exophase JSON exists (scrape if needed)
            if (isRom)
            {
                await EnsureExophaseAchievementsJsonAsync();
            }

            // Now load the achievements JSON if it exists
            if (File.Exists(cacheFile))
            {
                try
                {
                    // For Exophase, the JSON is an object with "Items" array
                    string json = await File.ReadAllTextAsync(cacheFile);
                    if (isRom)
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("Items", out var items))
                        {
                            achievements = items.EnumerateArray()
                                .Select(item => new AchievementVM
                                {
                                    Title = item.GetProperty("Title").GetString() ?? item.GetProperty("Name").GetString() ?? "",
                                    Description = item.TryGetProperty("Description", out var desc) ? desc.GetString() ?? "" : "",
                                    Platform = platform,
                                    UrlUnlocked = item.TryGetProperty("UrlUnlocked", out var url) ? url.GetString() : null,
                                    DateUnlocked = item.TryGetProperty("DateUnlocked", out var date) ? date.GetString() : null,
                                    IsHidden = item.TryGetProperty("IsHidden", out var hidden) && hidden.GetBoolean(),
                                    Percent = item.TryGetProperty("Percent", out var percent) ? percent.GetDouble() : 0.0,
                                    Category = item.TryGetProperty("Category", out var cat) ? cat.GetString() : null,
                                    IsUnlocked = item.TryGetProperty("IsUnlocked", out var unlocked) && unlocked.GetBoolean()
                                })
                                .ToList();
                        }
                    }
                    else
                    {
                        achievements = JsonSerializer.Deserialize<List<AchievementVM>>(json) ?? new List<AchievementVM>();
                    }

                    // If Steam PC game, merge emulator unlocks
                    if (isSteamGame)
                    {
                        UpdateAchievementsWithSteamEmulators(_vm, achievements);
                        File.WriteAllText(cacheFile, JsonSerializer.Serialize(achievements, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch
                {
                    achievements = new List<AchievementVM>();
                }
            }

            int total = achievements.Count;
            int unlocked = achievements.Count(a => a.IsUnlocked);

            Dispatcher.Invoke(() => UpdateAchievementProgress(unlocked, total));
        }

        private void UpdateAchievementsWithSteamEmulators(GameAppTileVM game, List<AchievementVM> merged)
        {
            if (string.IsNullOrWhiteSpace(game?.SteamAppId) || merged == null || merged.Count == 0)
                return;

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                var emuPaths = new List<string>
                {
                    Path.Combine(appData, "GSE Saves", game.SteamAppId, "achievements.json"),
                    Path.Combine(appData, "Goldberg SteamEmu Saves", game.SteamAppId, "achievements.json"),
                    Path.Combine(appData, "SmartSteamEmu", game.SteamAppId, "achievements.json"),
                    Path.Combine(appData, "Steam", "Codex", game.SteamAppId, "achievements.json")
                };

                foreach (var emuPath in emuPaths)
                {
                    if (!File.Exists(emuPath))
                        continue;

                    try
                    {
                        string json = File.ReadAllText(emuPath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        foreach (var prop in root.EnumerateObject())
                        {
                            var earned = prop.Value.TryGetProperty("earned", out var earnedProp) && earnedProp.GetBoolean();
                            var earnedTime = prop.Value.TryGetProperty("earned_time", out var timeProp) ? timeProp.GetInt64() : 0;
                            var match = merged.FirstOrDefault(a => a.Title.Equals(prop.Name, StringComparison.OrdinalIgnoreCase));
                            if (match != null && earned)
                            {
                                match.IsUnlocked = true;
                                match.Percent = 100.0;
                                if (earnedTime > 0)
                                    match.DateUnlocked = DateTimeOffset.FromUnixTimeSeconds(earnedTime).ToString("g");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Helper: Load platform achievements (for non-Steam games)
        private List<AchievementVM> LoadPlatformAchievements(string accountName, string platform, string gameName)
        {
            try
            {
                string achievementsPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Data", "Accounts", accountName, "Achievements", platform, gameName, $"{gameName}.json");

                if (!File.Exists(achievementsPath))
                    return new List<AchievementVM>();

                using var stream = File.OpenRead(achievementsPath);
                using var doc = JsonDocument.Parse(stream);

                var items = doc.RootElement.GetProperty("Items");
                var achievements = new List<AchievementVM>();

                foreach (var item in items.EnumerateArray())
                {
                    achievements.Add(new AchievementVM
                    {
                        Title = item.GetProperty("Name").GetString() ?? "",
                        Description = item.TryGetProperty("Description", out var desc) ? desc.GetString() ?? "" : "",
                        Platform = platform,
                        UrlUnlocked = item.TryGetProperty("UrlUnlocked", out var url) ? url.GetString() : null,
                        DateUnlocked = item.TryGetProperty("DateUnlocked", out var date) ? date.GetString() : null,
                        IsHidden = item.TryGetProperty("IsHidden", out var hidden) && hidden.GetBoolean(),
                        Percent = item.TryGetProperty("Percent", out var percent) ? percent.GetDouble() : 0.0,
                        Category = item.TryGetProperty("Category", out var cat) ? cat.GetString() : null,
                        IsUnlocked = item.TryGetProperty("IsUnlocked", out var unlocked) && unlocked.GetBoolean()
                    });
                }

                return achievements;
            }
            catch
            {
                return new List<AchievementVM>();
            }
        }

        // Fallback parser for Steam API response (from AchievementsPage)
        private List<AchievementVM> ParseSteamAchievementsJson(string json, out int total)
        {
            var achievements = new List<AchievementVM>();
            total = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerstats", out var playerstats) &&
                    playerstats.TryGetProperty("achievements", out var achArray))
                {
                    foreach (var ach in achArray.EnumerateArray())
                    {
                        achievements.Add(new AchievementVM
                        {
                            Title = ach.GetProperty("apiname").GetString() ?? "",
                            Description = ach.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                            Platform = "PC (Windows)",
                            IsUnlocked = ach.GetProperty("achieved").GetInt32() == 1,
                            Percent = ach.GetProperty("achieved").GetInt32() == 1 ? 100.0 : 0.0,
                            DateUnlocked = ach.TryGetProperty("unlocktime", out var unlock) && unlock.GetInt32() > 0
                                ? DateTimeOffset.FromUnixTimeSeconds(unlock.GetInt32()).ToString("g")
                                : null
                        });
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            total = achievements.Count;
            return achievements;
        }

        // 1. Add this helper to scrape the first magnet link from a FitGirl Repacks page
        private async Task<string> ScrapeMagnetUrlAsync(string fitgirlUrl)
        {
            using var http = new HttpClient();
            string html;
            try
            {
                html = await http.GetStringAsync(fitgirlUrl);
            }
            catch
            {
                return null;
            }
            var match = Regex.Match(html, "<a[^>]+href=[\"'](magnet:[^\"']+)[\"']", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        // 2. Add helpers to save/load Downloaded.Games.json
        // --- Replace the old SaveDownloadedGameUrl and LoadDownloadedGameUrl with these version-aware methods ---

        private void SaveDownloadedGameInfo(string gameName, string url, string version = null)
        {
            string accountDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", _userName);
            string jsonPath = Path.Combine(accountDir, "Downloaded.Games.json");
            Dictionary<string, Dictionary<string, string>> dict = new();
            if (File.Exists(jsonPath))
            {
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(jsonPath)) ?? new();
                }
                catch { }
            }
            if (!dict.ContainsKey(gameName))
                dict[gameName] = new Dictionary<string, string>();
            dict[gameName]["Url"] = url;
            if (!string.IsNullOrEmpty(version))
                dict[gameName]["Version"] = version;
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }

        private List<string> LoadDownloadedGameUrls(string gameName)
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "System", "Addons", "Store", "All.Games.json");
            var urls = new List<string>();
            if (!File.Exists(jsonPath))
            {
                Debug.WriteLine($"[LoadDownloadedGameUrls] File not found: {jsonPath}");
                return urls;
            }

            try
            {
                var json = File.ReadAllText(jsonPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<StoreGameEntry>>(json, options);

                string normName = NormalizeGameName(gameName);
                Debug.WriteLine($"[LoadDownloadedGameUrls] Searching for: '{gameName}' (Normalized: '{normName}')");

                if (games != null)
                {
                    foreach (var g in games)
                    {
                        string entryNorm = NormalizeGameName(g.Name);
                        Debug.WriteLine($"[LoadDownloadedGameUrls] Entry: '{g.Name}' (Normalized: '{entryNorm}')");

                        bool isMatch =
                            entryNorm.Equals(normName, StringComparison.OrdinalIgnoreCase) ||
                            entryNorm.Contains(normName, StringComparison.OrdinalIgnoreCase) ||
                            normName.Contains(entryNorm, StringComparison.OrdinalIgnoreCase);

                        if (isMatch)
                        {
                            Debug.WriteLine($"[LoadDownloadedGameUrls] MATCH: '{g.Name}'");

                            if (g.Url is JsonElement je)
                            {
                                if (je.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in je.EnumerateArray())
                                    {
                                        if (item.ValueKind == JsonValueKind.String)
                                        {
                                            string url = item.GetString();
                                            Debug.WriteLine($"[LoadDownloadedGameUrls]   URL: {url}");
                                            if (!string.IsNullOrWhiteSpace(url))
                                                urls.Add(url);
                                        }
                                    }
                                }
                                else if (je.ValueKind == JsonValueKind.String)
                                {
                                    var urlStr = je.GetString();
                                    Debug.WriteLine($"[LoadDownloadedGameUrls]   URL: {urlStr}");
                                    if (!string.IsNullOrWhiteSpace(urlStr))
                                        urls.Add(urlStr);
                                }
                            }
                            else if (g.Url is string urlStr)
                            {
                                Debug.WriteLine($"[LoadDownloadedGameUrls]   URL: {urlStr}");
                                if (!string.IsNullOrWhiteSpace(urlStr))
                                    urls.Add(urlStr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadDownloadedGameUrls] Exception: {ex}");
            }
            return urls;
        }
        private string LoadDownloadedGameVersion(string gameName)
        {
            string accountDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", _userName);
            string jsonPath = Path.Combine(accountDir, "Downloaded.Games.json");
            if (!File.Exists(jsonPath)) return null;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(jsonPath));
                if (dict != null && dict.TryGetValue(gameName, out var obj) && obj != null && obj.TryGetValue("Version", out var v))
                    return v;
            }
            catch { }
            return null;
        }
        // Improved: ScrapeMagnetAndVersionAsync - more robust version extraction from <h1 class="entry-title">...</h1>
        private async Task<(string Magnet, string Version)> ScrapeMagnetAndVersionAsync(string fitgirlUrl)
        {
            using var http = new HttpClient();
            string html;
            try
            {
                html = await http.GetStringAsync(fitgirlUrl);
            }
            catch
            {
                return (null, null);
            }

            // Find the first magnet link
            var magnetMatch = Regex.Match(html, "<a[^>]+href=[\"'](magnet:[^\"']+)[\"']", RegexOptions.IgnoreCase);
            string magnet = magnetMatch.Success ? magnetMatch.Groups[1].Value : null;

            // Find the version in the <h1 class="entry-title">...</h1>
            string version = null;
            var h1Match = Regex.Match(html, @"<h1[^>]*class\s*=\s*[""']entry-title[""'][^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (h1Match.Success)
            {
                var h1Text = h1Match.Groups[1].Value;

                // Try to find "v1.0.4", "v2.3", "v1.0.4b", "v1.0.4-RELOADED", etc.
                var versionMatch = Regex.Match(h1Text, @"\bv\s?(\d+(\.\d+)*([a-zA-Z0-9\-]+)?)", RegexOptions.IgnoreCase);
                if (versionMatch.Success)
                    version = "v" + versionMatch.Groups[1].Value;
                else
                {
                    // Fallback: try to find a year or other version-like pattern
                    var fallback = Regex.Match(h1Text, @"\b(\d{4}(?:\.\d+)*[a-zA-Z0-9\-]*)\b");
                    if (fallback.Success)
                        version = fallback.Groups[1].Value;
                }
            }

            return (magnet, version);
        }
        // Add this helper method to GameInfoPage to get the URL for a game from All.Games.json

        private List<string> GetStoreGameUrls(string gameName)
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "System", "Addons", "Store", "All.Games.json");
            var urls = new List<string>();
            if (!File.Exists(jsonPath))
                return urls;

            try
            {
                var json = File.ReadAllText(jsonPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<StoreGameEntry>>(json, options);

                string normName = NormalizeGameName(gameName);

                // More flexible matching: allow partial and normalized matches
                var matches = games?.Where(g =>
                    NormalizeGameName(g.Name).Equals(normName, StringComparison.OrdinalIgnoreCase) ||
                    NormalizeGameName(g.Name).Contains(normName, StringComparison.OrdinalIgnoreCase) ||
                    normName.Contains(NormalizeGameName(g.Name), StringComparison.OrdinalIgnoreCase)
                );

                if (matches != null)
                {
                    foreach (var match in matches)
                    {
                        if (match.Url is JsonElement je)
                        {
                            if (je.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in je.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.String)
                                        urls.Add(item.GetString());
                                }
                            }
                            else if (je.ValueKind == JsonValueKind.String)
                            {
                                var urlStr = je.GetString();
                                if (!string.IsNullOrWhiteSpace(urlStr))
                                    urls.Add(urlStr);
                            }
                        }
                        else if (match.Url is string urlStr)
                        {
                            if (!string.IsNullOrWhiteSpace(urlStr))
                                urls.Add(urlStr);
                        }
                    }
                }
            }
            catch { }
            return urls;
        }
        private async Task<string> ScrapeBuzzheavierDownloadAsync(string buzzheavierUrl)
        {
            try
            {
                string userDataDir = GetChromeUserDataDir();
                string chromePath = GetChromeExecutablePath();

                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = false,
                    Args = new[] { $"--user-data-dir={userDataDir}" }
                };
                if (!string.IsNullOrEmpty(chromePath))
                    launchOptions.ExecutablePath = chromePath;

                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
                var page = await browser.NewPageAsync();

                await page.GotoAsync(buzzheavierUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

                // Wait for the download button to be available
                var button = await page.WaitForSelectorAsync("a.link-button[hx-get]", new PageWaitForSelectorOptions { Timeout = 10000 });
                if (button == null)
                {
                    MessageBox.Show("Could not find a download button on the Buzzheavier page.", "Buzzheavier", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                // Wait for the download event after clicking
                var downloadTask = page.WaitForDownloadAsync();
                await button.ClickAsync();

                // Wait for download or timeout (30s)
                var completedTask = await Task.WhenAny(downloadTask, Task.Delay(30000));
                if (completedTask == downloadTask)
                {
                    var download = downloadTask.Result;
                    var tempPath = Path.Combine(Path.GetTempPath(), download.SuggestedFilename);
                    await download.SaveAsAsync(tempPath);

                    // Optionally open the file
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempPath,
                        UseShellExecute = true
                    });

                    return tempPath;
                }
                else
                {
                    MessageBox.Show("Timed out waiting for the download to start.", "Buzzheavier", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error scraping Buzzheavier: " + ex.Message, "Buzzheavier", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }
        private async Task<string> ScrapeSteamripForBuzzheavierAsync(string steamripUrl)
        {
            using var http = new HttpClient();
            string html;
            try
            {
                html = await http.GetStringAsync(steamripUrl);
            }
            catch
            {
                return null;
            }

            // Find the buzzheavier link
            var match = Regex.Match(html, @"<a[^>]+href\s*=\s*[""'](//buzzheavier\.com/[^""']+)[""'][^>]*class\s*=\s*[""'][^""']*shortc-button[^""']*[""'][^>]*>", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            string buzzheavierUrl = "https:" + match.Groups[1].Value;
            return buzzheavierUrl;
        }

        private async Task ScrapeExophaseAchievementsAsync(string exophaseUrl, string platform, string gameName)
        {
            if (string.IsNullOrWhiteSpace(exophaseUrl) || !exophaseUrl.Contains("exophase.com"))
                throw new ArgumentException("URL must be an Exophase achievement page.");

            string html;
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                var page = await browser.NewPageAsync();
                await page.GotoAsync(exophaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                html = await page.ContentAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load Exophase page (Playwright): {ex.Message}");
                return;
            }

            // Use HtmlAgilityPack for robust parsing
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var achievements = new List<AchievementVM>();
            var ul = doc.DocumentNode.SelectSingleNode("//ul[contains(@class,'achievement')]");
            if (ul == null)
            {
                MessageBox.Show("Could not find achievements list in the Exophase page.");
                return;
            }

            foreach (var li in ul.SelectNodes(".//li[contains(@class,'award')]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
            {
                // Title
                var title = li.SelectSingleNode(".//div[contains(@class,'award-title')]//a")?.InnerText?.Trim() ?? "";

                // Description
                var desc = li.SelectSingleNode(".//div[contains(@class,'award-description')]/p")?.InnerText?.Trim() ?? "";

                // Points
                var pointsStr = li.SelectSingleNode(".//div[contains(@class,'award-points')]/span")?.InnerText?.Trim() ?? "0";
                double.TryParse(pointsStr, out double points);

                // Image
                var imageUrl = li.SelectSingleNode(".//img")?.GetAttributeValue("src", null);

                // Unlock date
                var unlockDate = li.SelectSingleNode(".//div[contains(@class,'award-earned')]//span")?.InnerText?.Trim();

                // Locked/Unlocked
                bool isUnlocked = !li.GetClasses().Any(c => c.Contains("locked", StringComparison.OrdinalIgnoreCase));

                achievements.Add(new AchievementVM
                {
                    Title = title,
                    Description = desc,
                    Platform = platform,
                    Percent = 0,
                    UrlUnlocked = imageUrl,
                    DateUnlocked = string.IsNullOrWhiteSpace(unlockDate) || unlockDate.Contains("Earned offline") ? null : unlockDate,
                    IsUnlocked = isUnlocked,
                });
            }

            // Write to JSON
            string user = _mainWindow?.Username ?? "Default";
            string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", user, "Achievements", platform, gameName);
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, $"{gameName}.json");

            var jsonObj = new
            {
                Game = gameName,
                Platform = platform,
                Items = achievements
            };

            var json = JsonSerializer.Serialize(jsonObj, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);

            MessageBox.Show($"Scraped {achievements.Count} achievements from Exophase and saved to:\n{outPath}", "Exophase Scraper", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private async Task EnsureExophaseAchievementsJsonAsync()
        {
            string platform = GetPlatform(_vm);
            string user = _mainWindow?.Username ?? "Default";
            string achDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Accounts", user, "Achievements", platform, _vm.Name);
            string achPath = Path.Combine(achDir, $"{_vm.Name}.json");

            if (File.Exists(achPath))
                return; // Already exists

            // Check all known URLs for an Exophase link
            var urls = LoadDownloadedGameUrls(_vm.Name)
                .Concat(GetStoreGameUrls(_vm.Name))
                .Distinct()
                .ToList();

            string exophaseUrl = urls.FirstOrDefault(u => u.Contains("exophase.com", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(exophaseUrl))
                return; // No Exophase URL found

            await ScrapeExophaseAchievementsAsync(exophaseUrl, platform, _vm.Name);
        }

        private string GetChromeUserDataDir()
        {
            // Use the "Default" profile or a custom one if you wish
            string userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Default"
            );
            if (!Directory.Exists(userDataDir))
                throw new DirectoryNotFoundException($"Chrome user data directory not found: {userDataDir}");
            return userDataDir;
        }

        private string GetChromeExecutablePath()
        {
            // Try common install locations for Chrome on Windows
            string[] possiblePaths = new[]
            {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
    };
            foreach (var path in possiblePaths)
                if (File.Exists(path))
                    return path;
            // Fallback: let Playwright use its own Chromium if not found
            return null;
        }


    }
}

