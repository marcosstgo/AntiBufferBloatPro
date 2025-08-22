using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
// Tray (WinForms)
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AntiBufferBloatPro
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
        private static readonly HttpClient _http = new HttpClient();

        // Serie para la gráfica (verde Discord)
        private readonly Polyline _series = new()
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3BA55D")),
            StrokeThickness = 2,
            Opacity = 0.95
        };

        private const int MaxPoints = 120;
        private readonly System.Collections.Generic.Queue<PlotPoint> _points = new();

        // Tray
        private WinForms.NotifyIcon? _tray;
        private const int AlertWindow = 5;
        private const int AlertThresholdMs = 60;

        // Backup TCP
        private string? tcpBackup;

        public MainWindow()
        {
            InitializeComponent();

            // Gráfica
            PingCanvas.Children.Add(_series);
            SizeChanged += (_, __) => RedrawSeries();

            // Timer & carga
            Loaded += async (_, __) =>
            {
                if (!IsAdmin())
                    Log("⚠ La app no tiene privilegios de administrador. Algunos comandos netsh podrían fallar.");

                await RefreshStatusAsync();
                _timer.Start();
            };
            _timer.Tick += async (_, __) => await RefreshStatusAsync();

            // Tray
            InitTray();
        }

        private static bool IsAdmin()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ===== Status =====
        private async Task RefreshStatusAsync()
        {
            try
            {
                var ping = GetPingMs();
                lblPing.Text = $"Ping: {(ping >= 0 ? ping.ToString() : "--")} ms";
                if (ping >= 0) { AddPoint(ping); CheckLatencyAlert(); }

                lblIpExt.Text = "IP Ext: " + await GetExternalIpAsync();
                lblIpInt.Text = "IP Int: " + GetInternalIp();

                var (auto, rss) = GetTcpGlobal();
                lblAuto.Text = "Auto-Tuning: " + (auto ?? "--");
                lblRss.Text = "RSS: " + (rss ?? "--");

                tgAuto.IsChecked = (auto ?? "").StartsWith("normal", StringComparison.OrdinalIgnoreCase);
                tgRss.IsChecked = (rss ?? "").StartsWith("enabled", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log("Status error: " + ex.Message); }
        }

        private long GetPingMs()
        {
            try { using var p = new Ping(); var r = p.Send("1.1.1.1", 1500); return (r != null && r.Status == IPStatus.Success) ? r.RoundtripTime : -1; }
            catch { return -1; }
        }

        private async Task<string> GetExternalIpAsync()
        {
            try { return await _http.GetStringAsync("https://api.ipify.org"); }
            catch { return "--"; }
        }

        private string GetInternalIp()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "--";
            }
            catch { return "--"; }
        }

        private (string? autotune, string? rss) GetTcpGlobal()
        {
            var text = RunCmd("netsh", "int tcp show global");
            string? auto = null, rss = null;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("Receive Window Auto-Tuning Level", StringComparison.OrdinalIgnoreCase))
                    auto = line.Split(':').Last().Trim();
                if (line.StartsWith("Receive-Side Scaling State", StringComparison.OrdinalIgnoreCase))
                    rss = line.Split(':').Last().Trim();
            }
            return (auto, rss);
        }

        // ===== Gráfica =====
        private readonly struct PlotPoint
        {
            public PlotPoint(int x, long y) { X = x; Y = y; }
            public int X { get; }
            public long Y { get; }
        }

        private void AddPoint(long ms)
        {
            if (_points.Count >= MaxPoints) _points.Dequeue();
            _points.Enqueue(new PlotPoint(_points.Count + 1, ms));
            RedrawSeries();
        }

        private void RedrawSeries()
        {
            _series.Points.Clear();
            if (_points.Count == 0) return;

            double w = Math.Max(10, PingCanvas.ActualWidth - 10);
            double h = Math.Max(10, PingCanvas.ActualHeight - 10);

            double maxX = Math.Max(1, _points.Count);
            double maxY = Math.Max(10, _points.Max(p => p.Y));
            double minY = Math.Max(0, _points.Min(p => p.Y));
            double rangeY = Math.Max(10, maxY - minY);

            int i = 0;
            foreach (var p in _points)
            {
                double x = (i / (maxX - 1)) * w;
                double y = h - ((p.Y - minY) / rangeY) * h;
                _series.Points.Add(new System.Windows.Point(x + 5, y + 5));
                i++;
            }
        }

        private void CheckLatencyAlert()
        {
            if (_points.Count < AlertWindow) return;
            var last = _points.Reverse().Take(AlertWindow).Select(p => (double)p.Y).ToArray();
            double avg = last.Average();
            if (avg > AlertThresholdMs && tgTray.IsChecked == true && _tray != null)
            {
                _tray.BalloonTipTitle = "Latencia alta";
                _tray.BalloonTipText = $"Promedio ~{avg:0} ms en últimos {AlertWindow} pings";
                _tray.ShowBalloonTip(3000);
            }
        }

        // ===== RunCmd / Log =====
        private string RunCmd(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                    // No 'Verb = "runas"' porque el manifest ya pide admin
                };
                using var p = Process.Start(psi)!;
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr.Trim());
                if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout.Trim());
                return stdout + Environment.NewLine + stderr;
            }
            catch (Exception ex) { Log("cmd error: " + ex.Message); return string.Empty; }
        }

        private string RunNetsh(string args) => RunCmd("netsh", args);

        private void Log(string msg)
        {
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
            txtLog.ScrollToEnd();
        }

        // ===== Perfiles =====
        private enum NetProfile { Gaming, Streaming, Browsing }

        private void ApplyProfile(NetProfile p)
        {
            string autotune = "normal", rss = "enabled", ecn = "disabled", cc = "cubic";
            string name = p.ToString();

            switch (p)
            {
                case NetProfile.Gaming: autotune = "disabled"; rss = "enabled"; ecn = "enabled"; cc = "ctcp"; name = "Gaming"; break;
                case NetProfile.Streaming: autotune = "normal"; rss = "enabled"; ecn = "enabled"; cc = "cubic"; name = "Streaming"; break;
                case NetProfile.Browsing: autotune = "normal"; rss = "enabled"; ecn = "disabled"; cc = "cubic"; name = "Browsing"; break;
            }

            RunNetsh($"int tcp set global autotuninglevel={autotune}");
            RunNetsh($"int tcp set global rss={rss}");
            RunNetsh($"int tcp set global ecncapability={ecn}");
            RunNetsh($"int tcp set supplemental template=internet congestionprovider={cc}");

            HighlightProfile(name);
            _ = RefreshStatusAsync();
        }

        private void HighlightProfile(string name)
        {
            var accent = (SolidColorBrush)FindResource("Accent");
            var secondary = (SolidColorBrush)FindResource("SecondaryBg");

            // Reset a gris
            btnGaming.Background = secondary;
            btnStreaming.Background = secondary;
            btnBrowsing.Background = secondary;

            // Activo en accent
            if (name == "Gaming") btnGaming.Background = accent;
            if (name == "Streaming") btnStreaming.Background = accent;
            if (name == "Browsing") btnBrowsing.Background = accent;

            txtProfile.Text = $"Perfil: {name}";
        }

        private void btnGaming_Click(object s, RoutedEventArgs e) => ApplyProfile(NetProfile.Gaming);
        private void btnStreaming_Click(object s, RoutedEventArgs e) => ApplyProfile(NetProfile.Streaming);
        private void btnBrowsing_Click(object s, RoutedEventArgs e) => ApplyProfile(NetProfile.Browsing);

        // ===== Acciones rápidas =====
        private void NormalAuto_Click(object sender, RoutedEventArgs e)
        {
            RunNetsh("int tcp set global autotuninglevel=normal");
            _ = RefreshStatusAsync();
        }

        private void DisableAuto_Click(object sender, RoutedEventArgs e)
        {
            RunNetsh("int tcp set global autotuninglevel=disabled");
            _ = RefreshStatusAsync();
        }

        private void EnableRss_Click(object sender, RoutedEventArgs e)
        {
            RunNetsh("int tcp set global rss=enabled");
            _ = RefreshStatusAsync();
        }

        private void DisableRss_Click(object sender, RoutedEventArgs e)
        {
            RunNetsh("int tcp set global rss=disabled");
            _ = RefreshStatusAsync();
        }

        // ===== Toggles =====
        private void tgEcn_Checked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global ecncapability=enabled");
        private void tgEcn_Unchecked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global ecncapability=disabled");
        private void tgRss_Checked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global rss=enabled");
        private void tgRss_Unchecked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global rss=disabled");
        private void tgAuto_Checked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global autotuninglevel=normal");
        private void tgAuto_Unchecked(object s, RoutedEventArgs e) => RunNetsh("int tcp set global autotuninglevel=disabled");

        // ===== Backup / Restore =====
        private void Backup_Click(object s, RoutedEventArgs e)
        {
            tcpBackup = RunNetsh("int tcp show global");
            MessageBox.Show("Backup TCP guardado en memoria.", "Backup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Restore_Click(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(tcpBackup))
            {
                MessageBox.Show("No hay backup previo.", "Restore",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string Find(string key, string def)
            {
                var line = tcpBackup!.Split('\n').FirstOrDefault(l => l.Contains(key, StringComparison.OrdinalIgnoreCase));
                return line?.Split(':').Last().Trim().ToLower() ?? def;
            }

            var auto = Find("Receive Window Auto-Tuning Level", "normal");
            var rss = Find("Receive-Side Scaling State", "enabled");
            var ecn = Find("ECN Capability", "disabled");

            RunNetsh($"int tcp set global autotuninglevel={auto}");
            RunNetsh($"int tcp set global rss={rss}");
            RunNetsh($"int tcp set global ecncapability={ecn}");
            RunNetsh($"int tcp set supplemental template=internet congestionprovider=cubic");
            _ = RefreshStatusAsync();
            MessageBox.Show("Ajustes TCP restaurados.", "Restore",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===== Diagnóstico =====
        private void Diag_Click(object s, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine(RunNetsh("int tcp show global"));
            sb.AppendLine(RunCmd("cmd.exe", "/C ipconfig /all"));
            sb.AppendLine(RunCmd("cmd.exe", "/C ping 1.1.1.1 -n 5"));

            var filePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "diag_antibb.txt"
            );
            File.WriteAllText(filePath, sb.ToString());
            MessageBox.Show($"Diagnóstico guardado:\n{filePath}", "Diagnóstico",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===== Links =====
        private void Speedtest_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.speedtest.net") { UseShellExecute = true });
        private void Bufferbloat_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.waveform.com/tools/bufferbloat") { UseShellExecute = true });

        // ===== Tray =====
        private void InitTray()
        {
            _tray = new WinForms.NotifyIcon
            {
                Visible = false,
                Text = "Anti-BufferBloat Pro",
                Icon = Drawing.SystemIcons.Application
            };
            _tray.DoubleClick += (_, __) => RestoreFromTray();

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Mostrar", null, (_, __) => RestoreFromTray());
            menu.Items.Add("Salir", null, (_, __) => { _tray!.Visible = false; Application.Current.Shutdown(); });
            _tray.ContextMenuStrip = menu;

            StateChanged += (_, __) =>
            {
                if (tgTray.IsChecked == true && WindowState == WindowState.Minimized)
                {
                    Hide();
                    _tray!.Visible = true;
                    _tray.BalloonTipTitle = "Anti-BufferBloat Pro";
                    _tray.BalloonTipText = "Corriendo en segundo plano.";
                    _tray.ShowBalloonTip(2000);
                }
            };
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_tray != null) _tray.Visible = false;
        }

        private void tgTray_Checked(object s, RoutedEventArgs e) { if (_tray != null) _tray.Visible = true; }
        private void tgTray_Unchecked(object s, RoutedEventArgs e) { if (_tray != null) _tray.Visible = false; }

        protected override void OnClosed(EventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnClosed(e);
        }
    }
}
