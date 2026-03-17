using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace AntiBufferBloatPro
{
    public partial class UpdateWindow : Window
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private readonly string _newVersion;
        private readonly string _downloadUrl;

        public UpdateWindow(string newVersion, string downloadUrl)
        {
            InitializeComponent();
            _newVersion = newVersion;
            _downloadUrl = downloadUrl;

            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            TxtCurrentVer.Text = $"v{current}";
            TxtNewVer.Text = $"v{newVersion}";
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdate.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            TxtStatus.Text = "Descargando...";

            try
            {
                var tempExe = Path.Combine(Path.GetTempPath(), $"AntiBufferBloatPro-update-{_newVersion}.exe");

                using var resp = await _http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;

                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(tempExe);
                var buf = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0)
                        ProgressBar.Value = (double)downloaded / total * 100;
                }

                var currentExe = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AntiBufferBloatPro.exe");

                var batchPath = Path.Combine(Path.GetTempPath(), "abp_update.bat");
                File.WriteAllText(batchPath,
                    $"@echo off\r\n" +
                    $"ping 127.0.0.1 -n 6 > nul\r\n" +
                    $":retry\r\n" +
                    $"move /Y \"{tempExe}\" \"{currentExe}\"\r\n" +
                    $"if errorlevel 1 (ping 127.0.0.1 -n 3 > nul & goto retry)\r\n" +
                    $"start \"\" \"{currentExe}\"\r\n" +
                    $"del \"%~f0\"\r\n");

                TxtStatus.Visibility = Visibility.Collapsed;
                SuccessBanner.Visibility = Visibility.Visible;
                ProgressBar.Value = 100;

                await Task.Delay(1500);

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batchPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                System.Windows.Application.Current.Dispatcher.Invoke(
                    () => System.Windows.Application.Current.Shutdown());
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
                BtnCancel.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
