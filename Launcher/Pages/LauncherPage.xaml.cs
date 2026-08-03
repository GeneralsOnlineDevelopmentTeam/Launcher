using LauncherWinUI.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace LauncherWinUI.Pages
{
    public partial class LauncherPage : Page
    {
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = HappyEyeballsConnectAsync
        });

        // Races IPv4 and IPv6 connections in parallel, returns whichever wins (RFC 6555 Happy Eyeballs).
        // Avoids the 20-30s stall that occurs when .NET tries IPv6 first and the network doesn't support it.
        private static async ValueTask<Stream> HappyEyeballsConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.Unspecified, ct);
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task<Stream> TryConnect(IPAddress address)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, raceCts.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            var tasks = addresses.Select(TryConnect).ToList();
            var exceptions = new List<Exception>();

            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                try
                {
                    var stream = await done;
                    raceCts.Cancel(); // cancel losing attempts
                    return stream;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Add(ex);
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new AggregateException("Could not connect to any address", exceptions);
        }
        private LauncherSettingsFile _launcherSettings = new();
        private bool _initializing = true;

        public LauncherPage()
        {
            InitializeComponent();
            Loaded += LauncherPage_Loaded;
        }

        private void LauncherPage_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeLaunchOptionsFromArgs();

            // Load logo
            try
            {
                LogoImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logonew.png"));
            }
            catch { }

            if (File.Exists("GeneralsOnlineZH_TestEnvironment.exe"))
                ClientSelectorPanel.Visibility = Visibility.Visible;

            CreateGODataFolder();

            LoadLauncherSettings();
            DefaultAnticheatToFirstPlugin();
            _initializing = false;

            _ = LoadServerStatsAsync();

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\GeneralsOnline");
                key?.SetValue("InstallPath", Directory.GetCurrentDirectory(), RegistryValueKind.String);
            }
            catch { }
        }

        private async Task LoadServerStatsAsync()
        {
            try
            {
                string json = await _httpClient.GetStringAsync("https://api.playgenerals.online/env/prod/contract/1/Monitoring/BasicStats");
                var data = System.Text.Json.JsonSerializer.Deserialize<ServerStats>(json);
                if (data != null)
                {
                    string anticheatInfo = GetAnticheatDisplayText();
                    StatsLabel.Text = $"{data.Players} Players Online  •  {data.Lobbies} Lobbies{anticheatInfo}";
                }
            }
            catch
            {
                // Silently ignore if the server is unreachable
            }
        }

        private string GetAnticheatDisplayText()
        {
            try
            {
                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Command and Conquer Generals Zero Hour Data",
                    "GeneralsOnlineData",
                    "settings.json");

                if (File.Exists(filePath))
                {
                    var settings = System.Text.Json.JsonSerializer.Deserialize<GameSettingsFile>(
                        File.ReadAllText(filePath));
                    
                    if (settings?.plugins?.anticheat != null && !string.IsNullOrEmpty(settings.plugins.anticheat))
                    {
                        // Get the display name from the plugin's JSON file
                        string pluginFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", settings.plugins.anticheat);
                        var jsonFiles = Directory.GetFiles(pluginFolderPath, "*.json")
                            .OrderBy(f => Path.GetFileName(f))
                            .FirstOrDefault();
                        
                        if (jsonFiles != null)
                        {
                            var jsonData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                                File.ReadAllText(jsonFiles));
                            
                            if (jsonData != null && jsonData.TryGetValue("plugin_name", out var pluginName))
                            {
                                return $"  •  AntiCheat: {pluginName}";
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently fail if can't load anticheat info
            }

            return "";
        }

        private void CreateGODataFolder()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Command and Conquer Generals Zero Hour Data",
                "GeneralsOnlineData");
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private void LoadLauncherSettings()
        {
            try
            {
                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Command and Conquer Generals Zero Hour Data",
                    "GeneralsOnlineData",
                    "launcher.json");

                if (!File.Exists(filePath))
                {
                    _launcherSettings = new LauncherSettingsFile();
                    File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(
                        _launcherSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    _launcherSettings = System.Text.Json.JsonSerializer.Deserialize<LauncherSettingsFile>(
                        File.ReadAllText(filePath)) ?? new();
                }

                rbTestEnv.IsChecked = !_launcherSettings.prefer_experiemental_client;
                rbLiveClient.IsChecked = _launcherSettings.prefer_experiemental_client;

                LaunchOptions.Windowed = _launcherSettings.windowed;
                LaunchOptions.WindowedWidth = _launcherSettings.windowed_width;
                LaunchOptions.WindowedHeight = _launcherSettings.windowed_height;
            }
            catch
            {
                MessageBox.Show("Failed to load launcher settings.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DefaultAnticheatToFirstPlugin()
        {
            try
            {
                string settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Command and Conquer Generals Zero Hour Data",
                    "GeneralsOnlineData",
                    "settings.json");

                if (!File.Exists(settingsPath))
                    return;

                var settings = System.Text.Json.JsonSerializer.Deserialize<GameSettingsFile>(
                    File.ReadAllText(settingsPath));

                if (settings?.plugins == null)
                    return;

                string pluginsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
                
                if (!Directory.Exists(pluginsPath))
                    return;

                // Check if current anticheat's plugin still exists with valid DLL
                if (!string.IsNullOrEmpty(settings.plugins.anticheat))
                {
                    string currentPluginPath = Path.Combine(pluginsPath, settings.plugins.anticheat);
                    var dllFiles = Directory.GetFiles(currentPluginPath, "*.dll");
                    
                    // If plugin folder exists and has a DLL, keep it
                    if (Directory.Exists(currentPluginPath) && dllFiles.Any())
                        return;
                    
                    // Plugin or DLL is missing, will default to first below
                }

                var firstPlugin = Directory.GetDirectories(pluginsPath)
                    .Where(dir => Directory.GetFiles(dir, "*.json").Any() && Directory.GetFiles(dir, "*.dll").Any())
                    .OrderBy(dir => new DirectoryInfo(dir).Name)
                    .FirstOrDefault();

                if (firstPlugin != null)
                {
                    settings.plugins.anticheat = new DirectoryInfo(firstPlugin).Name;
                    File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(
                        settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch
            {
                // silently fail if settings file doesn't exist or can't be read
            }
        }

        private void SaveLauncherSettings()
        {
            try
            {
                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Command and Conquer Generals Zero Hour Data",
                    "GeneralsOnlineData",
                    "launcher.json");

                _launcherSettings.prefer_experiemental_client = rbLiveClient.IsChecked == true;
                File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(
                    _launcherSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void ClientSelector_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initializing)
                SaveLauncherSettings();
        }

        private void BtnOptions_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.Navigate(new OptionsPage());
        }

        private static void InitializeLaunchOptionsFromArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("-win", StringComparison.OrdinalIgnoreCase))
                    LaunchOptions.Windowed = true;
                else if (args[i].Equals("-xres", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                { if (int.TryParse(args[i + 1], out int w)) LaunchOptions.WindowedWidth = w; }
                else if (args[i].Equals("-yres", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                { if (int.TryParse(args[i + 1], out int h)) LaunchOptions.WindowedHeight = h; }
            }
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] incoming = Environment.GetCommandLineArgs()[1..];
                bool hasWin  = incoming.Any(a => a.Equals("-win",  StringComparison.OrdinalIgnoreCase));
                bool hasXRes = incoming.Any(a => a.Equals("-xres", StringComparison.OrdinalIgnoreCase));
                bool hasYRes = incoming.Any(a => a.Equals("-yres", StringComparison.OrdinalIgnoreCase));

                var argParts = incoming.ToList();

                if (LaunchOptions.Windowed && !hasWin)  argParts.Add("-win");
                if (LaunchOptions.Windowed && !hasXRes) { argParts.Add("-xres"); argParts.Add(LaunchOptions.WindowedWidth.ToString()); }
                if (LaunchOptions.Windowed && !hasYRes) { argParts.Add("-yres"); argParts.Add(LaunchOptions.WindowedHeight.ToString()); }

                string arguments = string.Join(" ", argParts);

                bool isEasyAntiCheat = false;
                try
                {
                    string settingsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Command and Conquer Generals Zero Hour Data",
                        "GeneralsOnlineData",
                        "settings.json");

                    if (File.Exists(settingsPath))
                    {
                        var settings = System.Text.Json.JsonSerializer.Deserialize<GameSettingsFile>(
                            File.ReadAllText(settingsPath));
                        
                        isEasyAntiCheat = settings?.plugins?.anticheat?.Equals("easyanticheat", StringComparison.OrdinalIgnoreCase) == true;
                    }
                }
                catch { }

                string exe;
                if (ClientSelectorPanel.Visibility == Visibility.Visible)
                    exe = rbTestEnv.IsChecked == true ? (isEasyAntiCheat ? "EAC_LaunchGeneralsOnline.exe" : "GeneralsOnlineZH_TestEnvironment.exe") : "GeneralsOnlineZH_60.exe";
                else
                    exe = isEasyAntiCheat ? "EAC_LaunchGeneralsOnline.exe" : "GeneralsOnlineZH_60.exe";

                var startedProcess = Process.Start(exe, arguments);

                // Hide the launcher while the game runs, then exit once the game closes.
                Application.Current.MainWindow?.Hide();

                if (startedProcess != null)
                    await startedProcess.WaitForExitAsync();

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Application.Current.MainWindow?.Show();
                MessageBox.Show($"Failed to launch game: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }

    file record ServerStats(
        [property: System.Text.Json.Serialization.JsonPropertyName("players")] int Players,
        [property: System.Text.Json.Serialization.JsonPropertyName("lobbies")] int Lobbies);
}
