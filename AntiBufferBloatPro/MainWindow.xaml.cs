using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AntiBufferBloatPro.Models;
using AntiBufferBloatPro.Services;
// Tray (WinForms)
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AntiBufferBloatPro
{
    public partial class MainWindow : Window
    {
        #region Constantes y Campos
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private static readonly HttpClient _httpClient = new HttpClient();

        // Serie para la gráfica (cyan neón gamer)
        private readonly Polyline _pingSeries = new()
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00F0FF")),
            StrokeThickness = 2,
            Opacity = 0.95,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#00F0FF"),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };

        // Área bajo la curva con gradiente
        private readonly Polygon _pingArea = new()
        {
            Opacity = 0.15,
            Fill = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#00F0FF"),
                Colors.Transparent,
                new System.Windows.Point(0, 0),
                new System.Windows.Point(0, 1))
        };

        private const int MAX_POINTS = 120;
        private const int ALERT_WINDOW = 5;
        private const int ALERT_THRESHOLD_MS = 60;
        private const int JITTER_SAMPLE_COUNT = 10;
        private const int PACKET_LOSS_SAMPLE_COUNT = 20;

        private readonly System.Collections.Generic.Queue<PlotPoint> _pingPoints = new();
        private long _lastPingMs = -1;
        private int _totalPingSent = 0;
        private int _totalPingLost = 0;

        // Tray
        private WinForms.NotifyIcon? _trayIcon;

        // Backup TCP
        private string? _tcpBackup;
        private static readonly string _backupFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AntiBufferBloatPro", "tcp_backup.txt");

        // Auto-update
        private static readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
        private string? _latestVersion;
        private string? _latestDownloadUrl;

        // Variables para controlar la consola
        private bool _isConsoleExpanded = false;
        private const double EXPANDED_CONSOLE_HEIGHT = 400;

        // Suprimir eventos de toggle al setear programáticamente
        private bool _suppressToggleEvents = false;

        // Bufferbloat test
        private readonly BufferbloatTestService _testService = new();
        private readonly BufferbloatAnalyzer _analyzer = new();
        private CancellationTokenSource? _testCts;
        private BufferbloatTestResult? _lastTestResult;

        // History & Settings
        private readonly HistoryService _historyService = new();
        private readonly SettingsService _settingsService = new();
        private AppSettings _settings = new();
        #endregion

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/abp.png"));
            }
            catch { }
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
                RunVersion.Text = $"v{v}";
            }
            catch { }
            InitializeApplication();
        }

        #region Inicialización
        private void InitializeApplication()
        {
            // Gráfica - agregar área y línea
            PingCanvas.Children.Add(_pingArea);
            PingCanvas.Children.Add(_pingSeries);
            SizeChanged += (_, __) => RedrawSeries();

            // Timer & carga
            Loaded += async (_, __) =>
            {
                if (!IsRunningAsAdmin())
                    Log("⚠ Sin privilegios de admin. Algunos comandos netsh podrían fallar.");

                await RefreshStatusAsync();
                ReadGamingStatus();
                _statusTimer.Start();
                _ = CheckForUpdateAsync();
            };

            _statusTimer.Tick += async (_, __) => await RefreshStatusAsync();

            // Settings & History
            _settings = _settingsService.Load();
            ApplySettingsToUI();
            if (_settings.DimMode) ApplyDimMode(true);

            // Tray
            InitializeTrayIcon();
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region Estado y Monitoreo
        private async Task RefreshStatusAsync()
        {
            try
            {
                var pingMs = await Task.Run(() => GetPingMs());
                _totalPingSent++;
                if (pingMs < 0) _totalPingLost++;

                UpdatePingDisplay(pingMs);
                UpdateJitterDisplay(pingMs);
                UpdatePacketLossDisplay();
                UpdatePingStatsDisplay();

                if (pingMs >= 0)
                {
                    AddPoint(pingMs);
                    CheckLatencyAlert();
                }

                _lastPingMs = pingMs;

                await UpdateIpAddressesAsync();
                await Task.Run(() => UpdateTcpSettings());
            }
            catch (Exception ex)
            {
                Log($"[ERR] Actualización fallida: {ex.Message}");
            }
        }

        private long GetPingMs()
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(_settings.PingTarget, 1500);
                return (reply != null && reply.Status == IPStatus.Success) ? reply.RoundtripTime : -1;
            }
            catch
            {
                return -1;
            }
        }

        private void UpdatePingDisplay(long pingMs)
        {
            lblPing.Text = $"{(pingMs >= 0 ? pingMs.ToString() : "--")} ms";

            if (pingMs >= 0)
            {
                if (pingMs < 30)
                    lblPing.Foreground = (SolidColorBrush)FindResource("NeonGreen");
                else if (pingMs < 60)
                    lblPing.Foreground = (SolidColorBrush)FindResource("NeonCyan");
                else if (pingMs < 100)
                    lblPing.Foreground = (SolidColorBrush)FindResource("NeonAmber");
                else
                    lblPing.Foreground = (SolidColorBrush)FindResource("NeonRed");
            }
        }

        private void UpdateJitterDisplay(long pingMs)
        {
            if (pingMs >= 0 && _lastPingMs >= 0)
            {
                long jitter = Math.Abs(pingMs - _lastPingMs);
                lblJitter.Text = $"{jitter} ms";

                if (jitter < 5)
                    lblJitter.Foreground = (SolidColorBrush)FindResource("NeonGreen");
                else if (jitter < 15)
                    lblJitter.Foreground = (SolidColorBrush)FindResource("NeonCyan");
                else if (jitter < 30)
                    lblJitter.Foreground = (SolidColorBrush)FindResource("NeonAmber");
                else
                    lblJitter.Foreground = (SolidColorBrush)FindResource("NeonRed");
            }
            else
            {
                lblJitter.Text = "-- ms";
            }
        }

        private void UpdatePacketLossDisplay()
        {
            if (_totalPingSent > 0)
            {
                double lossPercent = (_totalPingLost * 100.0) / _totalPingSent;
                lblPacketLoss.Text = $"{lossPercent:0.0}%";

                if (lossPercent < 1)
                    lblPacketLoss.Foreground = (SolidColorBrush)FindResource("NeonGreen");
                else if (lossPercent < 5)
                    lblPacketLoss.Foreground = (SolidColorBrush)FindResource("NeonAmber");
                else
                    lblPacketLoss.Foreground = (SolidColorBrush)FindResource("NeonRed");
            }
            else
            {
                lblPacketLoss.Text = "--%";
            }
        }

        private void UpdatePingStatsDisplay()
        {
            if (_pingPoints.Count > 0)
            {
                var values = _pingPoints.Select(p => p.Y).ToArray();
                lblPingMin.Text = $"{values.Min()} ms";
                lblPingMax.Text = $"{values.Max()} ms";
                lblPingAvg.Text = $"{values.Average():0} ms";
            }
        }

        private async Task UpdateIpAddressesAsync()
        {
            lblIpExt.Text = await GetExternalIpAsync();
            lblIpInt.Text = GetInternalIp();
        }

        private async Task<string> GetExternalIpAsync()
        {
            try
            {
                return await _httpClient.GetStringAsync("https://api.ipify.org");
            }
            catch
            {
                return "--";
            }
        }

        private string GetInternalIp()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "--";
            }
            catch
            {
                return "--";
            }
        }

        private void UpdateTcpSettings()
        {
            var (auto, rss) = GetTcpGlobalSettings();
            Dispatcher.Invoke(() =>
            {
                lblAuto.Text = auto ?? "--";
                lblRss.Text = rss ?? "--";

                tgAuto.IsChecked = (auto ?? "").StartsWith("normal", StringComparison.OrdinalIgnoreCase);
                tgRss.IsChecked = (rss ?? "").StartsWith("enabled", StringComparison.OrdinalIgnoreCase);
            });
        }

        private (string? autotune, string? rss) GetTcpGlobalSettings()
        {
            var text = RunNetshCommand("show global");
            string? auto = null, rss = null;

            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Receive Window Auto-Tuning Level", StringComparison.OrdinalIgnoreCase))
                    auto = line.Split(':').Last().Trim();
                if (line.StartsWith("Receive-Side Scaling State", StringComparison.OrdinalIgnoreCase))
                    rss = line.Split(':').Last().Trim();
            }

            return (auto, rss);
        }
        #endregion

        #region Gráfica de Ping
        private readonly struct PlotPoint
        {
            public PlotPoint(int x, long y) { X = x; Y = y; }
            public int X { get; }
            public long Y { get; }
        }

        private void AddPoint(long ms)
        {
            if (_pingPoints.Count >= MAX_POINTS)
                _pingPoints.Dequeue();

            _pingPoints.Enqueue(new PlotPoint(_pingPoints.Count + 1, ms));
            RedrawSeries();
        }

        private void RedrawSeries()
        {
            _pingSeries.Points.Clear();
            _pingArea.Points.Clear();

            if (_pingPoints.Count == 0)
                return;

            double canvasWidth = Math.Max(10, PingCanvas.ActualWidth - 10);
            double canvasHeight = Math.Max(10, PingCanvas.ActualHeight - 10);

            double maxX = Math.Max(1, _pingPoints.Count);
            double maxY = Math.Max(10, _pingPoints.Max(p => p.Y));
            double minY = Math.Max(0, _pingPoints.Min(p => p.Y));
            double rangeY = Math.Max(10, maxY - minY);

            int index = 0;
            foreach (var point in _pingPoints)
            {
                double x = (index / (maxX - 1)) * canvasWidth;
                double y = canvasHeight - ((point.Y - minY) / rangeY) * canvasHeight;
                var pt = new System.Windows.Point(x + 5, y + 5);
                _pingSeries.Points.Add(pt);
                _pingArea.Points.Add(pt);
                index++;
            }

            // Cerrar el área bajo la curva
            if (_pingArea.Points.Count > 0)
            {
                _pingArea.Points.Add(new System.Windows.Point(_pingArea.Points.Last().X, canvasHeight + 5));
                _pingArea.Points.Add(new System.Windows.Point(_pingArea.Points.First().X, canvasHeight + 5));
            }
        }

        private void CheckLatencyAlert()
        {
            if (_pingPoints.Count < ALERT_WINDOW)
                return;

            var recentPoints = _pingPoints.Reverse().Take(ALERT_WINDOW).Select(p => (double)p.Y).ToArray();
            double average = recentPoints.Average();

            if (average > ALERT_THRESHOLD_MS && tgTray.IsChecked == true && _trayIcon != null)
            {
                _trayIcon.BalloonTipTitle = "⚠ LATENCIA ALTA";
                _trayIcon.BalloonTipText = $"Promedio: ~{average:0} ms en últimos {ALERT_WINDOW} mediciones";
                _trayIcon.ShowBalloonTip(3000);
            }
        }
        #endregion

        #region Ejecución de Comandos y Logging
        private string RunCommand(string fileName, string arguments)
        {
            try
            {
                var processInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(processInfo)!;
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(error))
                    Log($"[ERR] {error.Trim()}");
                if (!string.IsNullOrWhiteSpace(output))
                    Log($"[CMD] {output.Trim()}");

                return output + Environment.NewLine + error;
            }
            catch (Exception ex)
            {
                Log($"[ERR] Comando fallido: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<string> RunCommandAsync(string fileName, string arguments)
        {
            return await Task.Run(() => RunCommand(fileName, arguments));
        }

        private string RunNetshCommand(string args) => RunCommand("netsh", $"int tcp {args}");

        private async Task<string> RunNetshCommandAsync(string args) => await RunCommandAsync("netsh", $"int tcp {args}");

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
                txtLog.CaretIndex = txtLog.Text.Length;
                txtLog.ScrollToEnd();

                // Auto-scroll al final
                consoleScrollViewer.ScrollToEnd();
            });
        }
        #endregion

        #region Perfiles de Red
        private record ProfileSettings(
            string AutoTune, string Rss, string Ecn, string CongestionProvider,
            string Timestamps, int InitialRto, bool NonSackRttResiliency, bool DisableNagle,
            string DisplayName);

        private static readonly ProfileSettings GamingProfile = new(
            AutoTune: "restricted",
            Rss: "enabled",
            Ecn: "disabled",
            CongestionProvider: "cubic",
            Timestamps: "disabled",
            InitialRto: 1000,
            NonSackRttResiliency: true,
            DisableNagle: true,
            DisplayName: "GAMING");

        private static readonly ProfileSettings DefaultProfile = new(
            AutoTune: "normal",
            Rss: "enabled",
            Ecn: "disabled",
            CongestionProvider: "cubic",
            Timestamps: "disabled",
            InitialRto: 3000,
            NonSackRttResiliency: false,
            DisableNagle: false,
            DisplayName: "RESET");

        private async void ApplyNetworkProfile(ProfileSettings profile)
        {
            Log($"[SYS] Aplicando perfil: {profile.DisplayName}...");

            await RunNetshCommandAsync($"set global autotuninglevel={profile.AutoTune}");
            await RunNetshCommandAsync($"set global rss={profile.Rss}");
            await RunNetshCommandAsync($"set global ecncapability={profile.Ecn}");
            await RunNetshCommandAsync($"set global timestamps={profile.Timestamps}");
            await RunNetshCommandAsync($"set global initialRto={profile.InitialRto}");
            await RunNetshCommandAsync($"set global nonsackrttresiliency={(profile.NonSackRttResiliency ? "enabled" : "disabled")}");
            await RunNetshCommandAsync($"set supplemental template=internet congestionprovider={profile.CongestionProvider}");
            ApplyNagleSetting(disable: profile.DisableNagle);

            if (profile.DisplayName == "GAMING")
            {
                SetUltimatePowerPlan(true);
                SetXboxDvr(disable: true);
                _suppressToggleEvents = true;
                tgPowerPlan.IsChecked = true;
                tgXboxDvr.IsChecked = true;
                _suppressToggleEvents = false;
            }

            HighlightActiveProfile(profile.DisplayName);
            await RefreshStatusAsync();
            Log($"[OK] Perfil {profile.DisplayName} activado.");
        }

        private void ApplyNagleSetting(bool disable)
        {
            try
            {
                const string keyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                using var interfacesKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                if (interfacesKey == null) return;

                foreach (var subKeyName in interfacesKey.GetSubKeyNames())
                {
                    using var subKey = interfacesKey.OpenSubKey(subKeyName, writable: true);
                    if (subKey == null) continue;

                    if (disable)
                    {
                        subKey.SetValue("TcpAckFrequency", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        subKey.SetValue("TCPNoDelay", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    else
                    {
                        subKey.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
                        subKey.DeleteValue("TCPNoDelay", throwOnMissingValue: false);
                    }
                }

                Log(disable
                    ? "[TCP] Nagle deshabilitado (TcpAckFrequency=1, TCPNoDelay=1)"
                    : "[TCP] Nagle restaurado a valores por defecto");
            }
            catch (Exception ex)
            {
                Log($"[WARN] No se pudo modificar Nagle en el registro: {ex.Message}");
            }
        }

        private void HighlightActiveProfile(string profileName)
        {
            var secondaryBrush = (SolidColorBrush)FindResource("SecondaryBg");
            btnGaming.Background = secondaryBrush;
            btnReset.Background = secondaryBrush;

            if (profileName == "GAMING")
            {
                btnGaming.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#001A1F"));
                txtProfile.Foreground = (SolidColorBrush)FindResource("NeonCyan");
            }
            else
            {
                btnReset.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1500"));
                txtProfile.Foreground = (SolidColorBrush)FindResource("NeonAmber");
            }

            txtProfile.Text = $"PERFIL: {profileName}";
        }

        private void btnGaming_Click(object sender, RoutedEventArgs e) => ApplyNetworkProfile(GamingProfile);
        private void btnReset_Click(object sender, RoutedEventArgs e) => ApplyNetworkProfile(DefaultProfile);
        #endregion

        #region Acciones de TCP
        private void NormalAuto_Click(object sender, RoutedEventArgs e)
        {
            RunNetshCommand("set global autotuninglevel=normal");
            _ = RefreshStatusAsync();
            Log("[TCP] Auto-Tuning → normal. Corre el test de nuevo para validar el cambio.");
        }

        private void RestrictedAuto_Click(object sender, RoutedEventArgs e)
        {
            RunNetshCommand("set global autotuninglevel=restricted");
            _ = RefreshStatusAsync();
            Log("[TCP] Auto-Tuning → restricted. Corre el test de nuevo para validar el cambio.");
        }

        private void DisableAuto_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDisableAutoTuning()) return;
            RunNetshCommand("set global autotuninglevel=disabled");
            _ = RefreshStatusAsync();
            Log("[TCP] Auto-Tuning → disabled. Corre el test de nuevo para medir el impacto en latencia y velocidad.");
        }

        private static bool ConfirmDisableAutoTuning()
        {
            var result = MessageBox.Show(
                "⚠  Auto-Tuning DISABLED fija la ventana TCP en 64 KB.\n\n" +
                "Esto REDUCE el bufferbloat pero también puede reducir\n" +
                "la velocidad de descarga/subida en conexiones rápidas.\n\n" +
                "Se recomienda probar primero con el perfil GAMING\n" +
                "que usa 'restricted' — mejor balance sin sacrificar velocidad.\n\n" +
                "¿Continuar con DISABLED de todas formas?",
                "Advertencia — Auto-Tuning Disabled",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        private void EnableRss_Click(object sender, RoutedEventArgs e)
        {
            RunNetshCommand("set global rss=enabled");
            _ = RefreshStatusAsync();
        }

        private void DisableRss_Click(object sender, RoutedEventArgs e)
        {
            RunNetshCommand("set global rss=disabled");
            _ = RefreshStatusAsync();
        }
        #endregion

        #region Toggles
        private void tgRss_Checked(object sender, RoutedEventArgs e) => RunNetshCommand("set global rss=enabled");
        private void tgRss_Unchecked(object sender, RoutedEventArgs e) => RunNetshCommand("set global rss=disabled");
        private void tgAuto_Checked(object sender, RoutedEventArgs e)
        {
            RunNetshCommand("set global autotuninglevel=normal");
            Log("[TCP] Auto-Tuning → normal. Corre el test de nuevo para validar el cambio.");
        }

        private void tgAuto_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents) return;
            if (!ConfirmDisableAutoTuning())
            {
                _suppressToggleEvents = true;
                tgAuto.IsChecked = true;
                _suppressToggleEvents = false;
                return;
            }
            RunNetshCommand("set global autotuninglevel=disabled");
            Log("[TCP] Auto-Tuning → disabled. Corre el test de nuevo para medir el impacto en latencia y velocidad.");
        }
        #endregion

        #region Backup y Restauración
        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            _tcpBackup = RunNetshCommand("show global");

            try
            {
                var dir = System.IO.Path.GetDirectoryName(_backupFilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_backupFilePath, _tcpBackup);
                Log($"[OK] Backup guardado en: {_backupFilePath}");
            }
            catch (Exception ex)
            {
                Log($"[WARN] No se pudo guardar backup a disco: {ex.Message}");
            }

            Log("[OK] Configuración TCP respaldada.");
            MessageBox.Show($"Configuración TCP respaldada.\nArchivo: {_backupFilePath}", "Backup Exitoso",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_tcpBackup) && File.Exists(_backupFilePath))
            {
                try
                {
                    _tcpBackup = File.ReadAllText(_backupFilePath);
                    Log("[SYS] Backup cargado desde archivo.");
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(_tcpBackup))
            {
                MessageBox.Show("No hay un respaldo previo disponible.", "Restore Fallido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string GetSettingValue(string key, string defaultValue)
            {
                var line = _tcpBackup!.Split('\n')
                    .FirstOrDefault(l => l.Contains(key, StringComparison.OrdinalIgnoreCase));

                return line?.Split(':').Last().Trim().ToLower() ?? defaultValue;
            }

            var auto = GetSettingValue("Receive Window Auto-Tuning Level", "normal");
            var rss = GetSettingValue("Receive-Side Scaling State", "enabled");
            var ecn = GetSettingValue("ECN Capability", "disabled");

            Log("[SYS] Restaurando configuración TCP...");
            RunNetshCommand($"set global autotuninglevel={auto}");
            RunNetshCommand($"set global rss={rss}");
            RunNetshCommand($"set global ecncapability={ecn}");
            RunNetshCommand($"set supplemental template=internet congestionprovider=cubic");

            _ = RefreshStatusAsync();
            Log("[OK] Configuración TCP restaurada.");

            MessageBox.Show("Configuración TCP restaurada correctamente.", "Restore Exitoso",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Diagnóstico
        private void Diag_Click(object sender, RoutedEventArgs e)
        {
            Log("[SYS] Ejecutando diagnóstico completo...");

            var diagnosticInfo = new StringBuilder();
            diagnosticInfo.AppendLine(RunNetshCommand("show global"));
            diagnosticInfo.AppendLine(RunCommand("ipconfig", "/all"));
            diagnosticInfo.AppendLine(RunCommand("ping", "1.1.1.1 -n 5"));

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var filePath = System.IO.Path.Combine(desktopPath, "diagnostico_antibufferbloat.txt");

            File.WriteAllText(filePath, diagnosticInfo.ToString());

            Log($"[OK] Diagnóstico guardado: {filePath}");
            MessageBox.Show($"Diagnóstico guardado en:\n{filePath}", "Diagnóstico Completo",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Tray Icon
        private void InitializeTrayIcon()
        {
            Drawing.Icon trayIconImage;
            try
            {
                var sri = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/abp.ico"));
                trayIconImage = sri != null
                    ? new Drawing.Icon(sri.Stream)
                    : Drawing.SystemIcons.Application;
            }
            catch { trayIconImage = Drawing.SystemIcons.Application; }

            _trayIcon = new WinForms.NotifyIcon
            {
                Visible = false,
                Text = "Anti-BufferBloat Pro",
                Icon = trayIconImage
            };

            _trayIcon.DoubleClick += (_, __) => RestoreFromTray();

            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add("Mostrar", null, (_, __) => RestoreFromTray());
            contextMenu.Items.Add("Salir", null, (_, __) =>
            {
                _trayIcon!.Visible = false;
                Application.Current.Shutdown();
            });

            _trayIcon.ContextMenuStrip = contextMenu;

            StateChanged += (_, __) =>
            {
                if (tgTray.IsChecked == true && WindowState == WindowState.Minimized)
                {
                    Hide();
                    _trayIcon!.Visible = true;
                    _trayIcon.BalloonTipTitle = "Anti-BufferBloat Pro";
                    _trayIcon.BalloonTipText = "Ejecutándose en segundo plano";
                    _trayIcon.ShowBalloonTip(2000);
                }
            };
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();

            if (_trayIcon != null)
                _trayIcon.Visible = false;
        }

        private void tgTray_Checked(object sender, RoutedEventArgs e)
        {
            if (_trayIcon != null)
                _trayIcon.Visible = true;
        }

        private void tgTray_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_trayIcon != null)
                _trayIcon.Visible = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _trayIcon?.Dispose();
            _httpClient?.Dispose();
            base.OnClosed(e);
        }
        #endregion

        #region PC Gaming Optimizations
        private void ReadGamingStatus()
        {
            // Force ControlTemplate application — controls may be in a non-selected tab (not yet rendered)
            tgPowerPlan.ApplyTemplate();
            tgXboxDvr.ApplyTemplate();

            // Power plan
            var list = RunCommand("powercfg", "/list");
            bool isUltimate = list.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
            _suppressToggleEvents = true;
            tgPowerPlan.IsChecked = isUltimate;

            // Xbox DVR
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                var val = key?.GetValue("AppCaptureEnabled");
                tgXboxDvr.IsChecked = (val is int i && i == 0);
            }
            catch { tgXboxDvr.IsChecked = false; }
            _suppressToggleEvents = false;

            ReadHagsStatus();
        }

        private void ReadHagsStatus()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
                var val = key?.GetValue("HwSchMode");
                bool hagsOn = (val is int i && i == 2);
                lblHags.Text = hagsOn ? "✅  ACTIVO (requiere reinicio para cambios)" : "⛔  INACTIVO";
                lblHags.Foreground = hagsOn
                    ? (SolidColorBrush)FindResource("NeonGreen")
                    : (SolidColorBrush)FindResource("NeonRed");
            }
            catch
            {
                lblHags.Text = "Sin acceso al registro";
            }
        }

        private void tgPowerPlan_Checked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) SetUltimatePowerPlan(true);
        }

        private void tgPowerPlan_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) SetUltimatePowerPlan(false);
        }

        private void SetUltimatePowerPlan(bool activate)
        {
            if (activate)
            {
                Log("[SYS] Activando Ultimate Performance...");
                var list = RunCommand("powercfg", "/list");
                if (!list.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                    RunCommand("powercfg", "/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");

                // Re-read after possible creation
                list = RunCommand("powercfg", "/list");
                var guidLine = list.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
                var guid = guidLine?.Split("GUID: ").Skip(1).FirstOrDefault()?.Split(' ').FirstOrDefault()?.Trim();

                if (!string.IsNullOrEmpty(guid))
                {
                    RunCommand("powercfg", $"/setactive {guid}");
                    Log($"[OK] Ultimate Performance activado: {guid}");
                }
                else
                {
                    Log("[WARN] No se encontró GUID del plan Ultimate Performance.");
                }
            }
            else
            {
                Log("[SYS] Restaurando plan Balanced...");
                RunCommand("powercfg", "/setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
                Log("[OK] Plan Balanced activado.");
            }
        }

        private void tgXboxDvr_Checked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) SetXboxDvr(disable: true);
        }

        private void tgXboxDvr_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) SetXboxDvr(disable: false);
        }

        private void SetXboxDvr(bool disable)
        {
            try
            {
                using var key1 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\GameDVR", writable: true);
                key1?.SetValue("AppCaptureEnabled", disable ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);

                using var key2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"System\GameConfigStore", writable: true);
                key2?.SetValue("GameDVR_Enabled", disable ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);

                Log(disable ? "[OK] Xbox DVR deshabilitado" : "[OK] Xbox DVR habilitado");
            }
            catch (Exception ex)
            {
                Log($"[WARN] No se pudo modificar Xbox DVR: {ex.Message}");
            }
        }

        private void AddDefenderExclusion_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Seleccionar ejecutable del juego",
                Filter = "Ejecutables (*.exe)|*.exe|Todos los archivos (*.*)|*.*",
                InitialDirectory = @"C:\Program Files"
            };

            if (dlg.ShowDialog() == true)
            {
                var exePath = dlg.FileName;
                var exeName = System.IO.Path.GetFileName(exePath);
                var exeDir = System.IO.Path.GetDirectoryName(exePath);
                Log($"[SYS] Añadiendo exclusión Defender: {exeName}");

                RunCommand("powershell",
                    $"-NonInteractive -NoProfile -Command " +
                    $"\"Add-MpPreference -ExclusionProcess '{exeName}' -ErrorAction SilentlyContinue; " +
                    $"Add-MpPreference -ExclusionPath '{exeDir}' -ErrorAction SilentlyContinue\"");

                Log($"[OK] Exclusión Defender añadida: {exeName} + carpeta {exeDir}");
            }
        }
        #endregion

        #region Manejo de la Consola
        private void btnToggleConsole_Click(object sender, RoutedEventArgs e)
        {
            ToggleConsole();
        }

        private void ToggleConsole()
        {
            var consoleRow = MainGrid.RowDefinitions[2];

            if (_isConsoleExpanded)
            {
                consoleRow.Height = new GridLength(160);
                btnToggleConsole.Content = "▲";
            }
            else
            {
                consoleRow.Height = new GridLength(EXPANDED_CONSOLE_HEIGHT);
                btnToggleConsole.Content = "▼";
            }

            _isConsoleExpanded = !_isConsoleExpanded;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isConsoleExpanded)
            {
                var consoleRow = MainGrid.RowDefinitions[2];
                consoleRow.Height = new GridLength(160);
            }
        }
        #endregion

        #region Bufferbloat Test
        private async void btnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            _testCts = new CancellationTokenSource();
            btnAnalyze.IsEnabled = false;
            btnCancelTest.Visibility = Visibility.Visible;
            pnlResult.Visibility = Visibility.Collapsed;
            pnlPhases.Visibility = Visibility.Collapsed;
            pnlRecommendation.Visibility = Visibility.Collapsed;
            pbTest.Value = 0;
            Log("[TEST] Iniciando test de bufferbloat...");

            var progress = new Progress<TestProgressUpdate>(update =>
            {
                pbTest.Value = update.ProgressPercent;
                lblTestPhase.Text = update.PhaseName.ToUpper();
                lblTestDetail.Text = "  " + update.Detail;
                if (update.LatestLatencyMs.HasValue)
                    Log($"[{update.PhaseName}] {update.LatestLatencyMs} ms");
            });

            try
            {
                var result = await _testService.RunAsync(progress, _testCts.Token,
                    _settings.PhaseDurationSeconds, _settings.PingTarget);
                _lastTestResult = result;
                var rec = _analyzer.BuildRecommendation(result);
                _historyService.Save(result, rec.Reasoning);
                ApplyTestResult(result);
                Log($"[TEST] Completado — Grade: {result.Grade} · Cuello: {result.PrimaryBottleneck}");
            }
            catch (OperationCanceledException)
            {
                lblTestPhase.Text = "CANCELADO";
                lblTestDetail.Text = "  Test cancelado por el usuario.";
                pbTest.Value = 0;
                Log("[TEST] Cancelado.");
            }
            catch (Exception ex)
            {
                lblTestPhase.Text = "ERROR";
                lblTestDetail.Text = "  " + ex.Message;
                Log($"[TEST] Error: {ex.Message}");
            }
            finally
            {
                btnAnalyze.IsEnabled = true;
                btnCancelTest.Visibility = Visibility.Collapsed;
                _testCts?.Dispose();
                _testCts = null;
            }
        }

        private void btnCancelTest_Click(object sender, RoutedEventArgs e)
        {
            _testCts?.Cancel();
        }

        private void ApplyTestResult(BufferbloatTestResult r)
        {
            // Grade — color por letra
            var gradeColor = r.Grade switch
            {
                "A" => (SolidColorBrush)FindResource("NeonGreen"),
                "B" => (SolidColorBrush)FindResource("NeonCyan"),
                "C" => (SolidColorBrush)FindResource("NeonAmber"),
                "D" => (SolidColorBrush)FindResource("NeonAmber"),
                _   => (SolidColorBrush)FindResource("NeonRed")
            };
            lblGrade.Text = r.Grade;
            lblGrade.Foreground = gradeColor;
            gradeBox.BorderBrush = gradeColor;

            lblBottleneck.Text = r.PrimaryBottleneck;
            lblWorstIncrease.Text = $"{r.WorstLatencyIncreaseMs:0} ms";
            lblTestSummary.Text = r.Summary;
            FadeIn(pnlResult);

            // Fases
            SetPhase(lblIdleAvg, lblIdleP95, lblIdleJitter, lblIdleLoss, null, r.Idle);
            SetPhase(lblDlAvg, lblDlP95, lblDlJitter, lblDlLoss, lblDlSpeed, r.Download);
            SetPhase(lblUlAvg, lblUlP95, lblUlJitter, lblUlLoss, lblUlSpeed, r.Upload);
            FadeIn(pnlPhases);

            // Recomendación
            var rec = _analyzer.BuildRecommendation(r);
            lblRecommendation.Text = rec.Reasoning;
            FadeIn(pnlRecommendation);
        }

        private static void FadeIn(UIElement element)
        {
            element.Opacity = 0;
            element.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static void SetPhase(
            System.Windows.Controls.TextBlock avg, System.Windows.Controls.TextBlock p95,
            System.Windows.Controls.TextBlock jitter, System.Windows.Controls.TextBlock loss,
            System.Windows.Controls.TextBlock? speed, LoadTestPhaseResult phase)
        {
            avg.Text    = $"{phase.AverageLatencyMs:0.0} ms";
            p95.Text    = $"{phase.P95LatencyMs:0.0} ms";
            jitter.Text = $"{phase.JitterMs:0.0} ms";
            loss.Text   = $"{phase.PacketLossPercent:0.0}%";
            if (speed != null)
                speed.Text = phase.ThroughputMbps > 0 ? $"{phase.ThroughputMbps:0.0} Mbps" : "-- Mbps";
        }

        private void btnCopyResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastTestResult == null) return;
            var r = _lastTestResult;
            var rec = _analyzer.BuildRecommendation(r);
            var text =
                $"AntiBufferBloat Pro — Resultado\n" +
                $"Grade: {r.Grade} | Cuello: {r.PrimaryBottleneck} | Peor incremento: {r.WorstLatencyIncreaseMs:0} ms\n\n" +
                $"IDLE     → Avg: {r.Idle.AverageLatencyMs:0.0} ms | P95: {r.Idle.P95LatencyMs:0.0} ms | Jitter: {r.Idle.JitterMs:0.0} ms | Loss: {r.Idle.PacketLossPercent:0.0}%\n" +
                $"DOWNLOAD → Avg: {r.Download.AverageLatencyMs:0.0} ms | P95: {r.Download.P95LatencyMs:0.0} ms | Jitter: {r.Download.JitterMs:0.0} ms | Loss: {r.Download.PacketLossPercent:0.0}% | Speed: {r.Download.ThroughputMbps:0.0} Mbps\n" +
                $"UPLOAD   → Avg: {r.Upload.AverageLatencyMs:0.0} ms | P95: {r.Upload.P95LatencyMs:0.0} ms | Jitter: {r.Upload.JitterMs:0.0} ms | Loss: {r.Upload.PacketLossPercent:0.0}% | Speed: {r.Upload.ThroughputMbps:0.0} Mbps\n\n" +
                $"Recomendación: {rec.Reasoning}";
            Clipboard.SetText(text);
            Log("[TEST] Resultado copiado al clipboard.");
        }

        private void btnExportJson_Click(object sender, RoutedEventArgs e)
        {
            if (_lastTestResult == null) return;
            var rec = _analyzer.BuildRecommendation(_lastTestResult);
            var payload = new
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                grade = _lastTestResult.Grade,
                primaryBottleneck = _lastTestResult.PrimaryBottleneck,
                worstLatencyIncreaseMs = _lastTestResult.WorstLatencyIncreaseMs,
                idle = _lastTestResult.Idle,
                download = _lastTestResult.Download,
                upload = _lastTestResult.Upload,
                recommendation = rec.Reasoning,
                recommendDownloadLimitMbps = rec.DownloadLimitMbps,
                recommendUploadLimitMbps = rec.UploadLimitMbps
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var filePath = System.IO.Path.Combine(desktopPath, $"bufferbloat_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(filePath, json);
            Log($"[TEST] Exportado: {filePath}");
            MessageBox.Show($"Resultado exportado:\n{filePath}", "Export JSON", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Historial
        private void btnHistoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshHistory();

        private void btnHistoryClear_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("¿Eliminar todo el historial?", "Limpiar historial",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _historyService.ClearAll();
            RefreshHistory();
            Log("[HIST] Historial eliminado.");
        }

        private void RefreshHistory()
        {
            // Limpiar items previos (mantener solo el TextBlock de "vacío")
            while (historyList.Children.Count > 1)
                historyList.Children.RemoveAt(1);

            var entries = _historyService.LoadAll();
            lblHistoryEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var entry in entries)
            {
                var gradeColor = entry.Result.Grade switch
                {
                    "A" => (SolidColorBrush)FindResource("NeonGreen"),
                    "B" => (SolidColorBrush)FindResource("NeonCyan"),
                    "C" => (SolidColorBrush)FindResource("NeonAmber"),
                    "D" => (SolidColorBrush)FindResource("NeonAmber"),
                    _   => (SolidColorBrush)FindResource("NeonRed")
                };

                var card = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = (SolidColorBrush)FindResource("Border"),
                    BorderThickness = new Thickness(1),
                    Background = (SolidColorBrush)FindResource("Card"),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Grade
                var gradeBlock = new System.Windows.Controls.TextBlock
                {
                    Text = entry.Result.Grade,
                    FontSize = 26, FontWeight = FontWeights.Black,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = gradeColor,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(gradeBlock, 0);

                // Info
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = $"{entry.Timestamp:yyyy-MM-dd  HH:mm}",
                    FontFamily = new FontFamily("Consolas"), FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = (SolidColorBrush)FindResource("Text")
                });
                info.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = $"Cuello: {entry.Result.PrimaryBottleneck}  ·  +{entry.Result.WorstLatencyIncreaseMs:0} ms  ·  DL {entry.Result.Download.ThroughputMbps:0.0} Mbps  UL {entry.Result.Upload.ThroughputMbps:0.0} Mbps",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary")
                });
                info.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = entry.Recommendation,
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = (SolidColorBrush)FindResource("Muted"),
                    TextWrapping = TextWrapping.Wrap
                });
                Grid.SetColumn(info, 1);

                // Delete btn
                var delBtn = new Button
                {
                    Content = "✕", FontSize = 11,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Foreground = (SolidColorBrush)FindResource("Muted"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 2, 6, 2)
                };
                var filePath = entry.FilePath;
                delBtn.Click += (_, __) => { _historyService.Delete(filePath); RefreshHistory(); };
                Grid.SetColumn(delBtn, 2);

                grid.Children.Add(gradeBlock);
                grid.Children.Add(info);
                grid.Children.Add(delBtn);
                card.Child = grid;
                historyList.Children.Add(card);
            }
        }
        #endregion

        #region Config & Tema
        private void ApplySettingsToUI()
        {
            // Solo aplicar a elementos del tab CONFIG cuando ya están en el visual tree
            // (el tab no seleccionado tiene el elemento instanciado pero su template no está aplicado)
            if (txtPingTarget != null && txtPingTarget.IsLoaded)
                txtPingTarget.Text = _settings.PingTarget;

            if (cbPhaseDuration != null && cbPhaseDuration.IsLoaded)
                cbPhaseDuration.SelectedIndex = _settings.PhaseDurationSeconds switch
                {
                    10 => 0,
                    30 => 2,
                    _  => 1
                };

            if (tgDimMode != null && tgDimMode.IsLoaded)
            {
                _suppressToggleEvents = true;
                tgDimMode.IsChecked = _settings.DimMode;
                _suppressToggleEvents = false;
            }
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                ApplySettingsToUI();
                if (MainTabControl.SelectedIndex == 2)
                    RefreshHistory();
            }));
        }

        private void btnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            _settings.PingTarget = string.IsNullOrWhiteSpace(txtPingTarget.Text) ? "1.1.1.1" : txtPingTarget.Text.Trim();
            _settings.PhaseDurationSeconds = cbPhaseDuration.SelectedIndex switch
            {
                0 => 10,
                2 => 30,
                _ => 20
            };
            _settings.DimMode = tgDimMode.IsChecked == true;
            _settingsService.Save(_settings);
            Log($"[CFG] Guardado — Duración: {_settings.PhaseDurationSeconds}s · Target: {_settings.PingTarget} · Dim: {_settings.DimMode}");
        }

        private void tgDimMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) ApplyDimMode(true);
        }

        private void tgDimMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_suppressToggleEvents) ApplyDimMode(false);
        }

        private void ApplyDimMode(bool dim)
        {
            if (dim)
            {
                // Modo Dim: fondos más claros (gris oscuro suave)
                this.Resources["Bg"]           = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E2E"));
                this.Resources["Card"]         = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26263A"));
                this.Resources["CardElevated"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30304A"));
                this.Resources["Header"]       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181826"));
            }
            else
            {
                this.Resources["Bg"]           = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A0A0F"));
                this.Resources["Card"]         = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#12121A"));
                this.Resources["CardElevated"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A25"));
                this.Resources["Header"]       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#08080D"));
            }
        }
        #endregion

        #region Auto-Update
        private async Task CheckForUpdateAsync()
        {
            try
            {
                const string apiUrl = "https://api.github.com/repos/marcosstgo/AntiBufferBloatPro/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.UserAgent.ParseAdd("AntiBufferBloatPro/1.1.0");
                using var resp = await _updateHttp.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var latestVersion = tagName.TrimStart('v');
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

                string? downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }

                if (Version.TryParse(latestVersion, out var latest) &&
                    Version.TryParse(currentVersion, out var current) &&
                    latest > current && downloadUrl != null)
                {
                    _latestVersion = latestVersion;
                    _latestDownloadUrl = downloadUrl;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateBadgeText.Text = $"v{latestVersion} disponible";
                        UpdateBadge.Visibility = Visibility.Visible;
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"CheckForUpdateAsync error: {ex}"); }
        }

        private void UpdateBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_latestDownloadUrl == null) return;
            var win = new UpdateWindow(_latestVersion!, _latestDownloadUrl) { Owner = this };
            win.ShowDialog();
        }
        #endregion
    }
}