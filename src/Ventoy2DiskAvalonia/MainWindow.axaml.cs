using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Ventoy2DiskDotNet;

namespace Ventoy2DiskAvalonia
{
    public partial class MainWindow : Window
    {
        private List<PhysicalDisk> _disks = new List<PhysicalDisk>();
        private bool _confirmingInstall = false;
        private bool _confirmingUpdate = false;
        private bool _isWorking = false;

        public MainWindow()
        {
            InitializeComponent();
            SetupConsoleRedirect();

            RefreshButton.Click += (s, e) => _ = RefreshDrivesAsync();
            DeviceComboBox.SelectionChanged += (s, e) => DeviceSelected();
            StyleComboBox.SelectionChanged += (s, e) => ResetConfirmations();
            FsComboBox.SelectionChanged += (s, e) => ResetConfirmations();
            SecureBootCheckBox.IsCheckedChanged += (s, e) => ResetConfirmations();

            InstallButton.Click += InstallButton_Click;
            UpdateButton.Click += UpdateButton_Click;

            // Load target package version
            LoadPackageVersion();

            // Initial refresh
            _ = RefreshDrivesAsync();
        }

        private void SetupConsoleRedirect()
        {
            Console.SetOut(new TextBoxWriter(msg =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    LogTextBlock.Text += msg;
                    LogScrollViewer.ScrollToEnd();
                });
            }));
        }

        private void LoadPackageVersion()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string versionPath = Path.Combine(baseDir, "ventoy", "version");
                if (File.Exists(versionPath))
                {
                    PackageVersionLabel.Text = File.ReadAllText(versionPath).Trim();
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Warning loading package version: {ex.Message}\n");
            }
        }

        private async Task RefreshDrivesAsync()
        {
            if (_isWorking) return;

            SetUIEnabled(false);
            StatusLabel.Text = "Scanning physical drives...";
            WriteLog("Scanning physical drives...\n");

            DeviceComboBox.Items.Clear();
            ResetConfirmations();

            try
            {
                // Run disk scanning on background thread
                _disks = await Task.Run(() => DiskService.GetPhysicalDisks());

                foreach (var disk in _disks)
                {
                    DeviceComboBox.Items.Add(disk);
                }

                if (_disks.Count > 0)
                {
                    DeviceComboBox.SelectedIndex = 0;
                    StatusLabel.Text = $"Found {_disks.Count} physical drive(s).";
                }
                else
                {
                    StatusLabel.Text = "No physical drives found. Run as Administrator/root.";
                    WriteLog("Warning: No physical drives detected. Please ensure you are running with administrative rights.\n");
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Disk scan failed.";
                WriteLog($"Disk scan failed: {ex.Message}\n");
            }
            finally
            {
                SetUIEnabled(true);
            }
        }

        private void DeviceSelected()
        {
            ResetConfirmations();
            if (DeviceComboBox.SelectedItem is PhysicalDisk disk)
            {
                StatusLabel.Text = $"Checking Ventoy on {disk.SystemName}...";
                DeviceVersionLabel.Text = "Checking...";

                Task.Run(() =>
                {
                    var (version, secureBoot) = DiskService.DetectVentoyVersion(disk);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (string.IsNullOrEmpty(version))
                        {
                            DeviceVersionLabel.Text = "Not Installed";
                            DeviceVersionLabel.Foreground = Avalonia.Media.Brushes.Gray;
                        }
                        else
                        {
                            string sbInfo = secureBoot ? "SecureBoot" : "NoSecureBoot";
                            DeviceVersionLabel.Text = $"{version} ({sbInfo})";
                            DeviceVersionLabel.Foreground = Avalonia.Media.Brushes.Green;
                        }
                        StatusLabel.Text = "Ready";
                    });
                });
            }
            else
            {
                DeviceVersionLabel.Text = "-";
            }
        }

        private void ResetConfirmations()
        {
            _confirmingInstall = false;
            _confirmingUpdate = false;
            InstallButton.Content = "Install";
            InstallButton.Background = Avalonia.Media.Brush.Parse("#8E1C1C");
            UpdateButton.Content = "Update";
            UpdateButton.Background = Avalonia.Media.Brush.Parse("#1C5E8E");
        }

        private void SetUIEnabled(bool enabled)
        {
            DeviceComboBox.IsEnabled = enabled;
            RefreshButton.IsEnabled = enabled;
            StyleComboBox.IsEnabled = enabled;
            FsComboBox.IsEnabled = enabled;
            SecureBootCheckBox.IsEnabled = enabled;
            InstallButton.IsEnabled = enabled;
            UpdateButton.IsEnabled = enabled;
            _isWorking = !enabled;
        }

        private void InstallButton_Click(object? sender, RoutedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is not PhysicalDisk disk) return;

            if (!_confirmingInstall)
            {
                _confirmingInstall = true;
                _confirmingUpdate = false;
                UpdateButton.Content = "Update";
                UpdateButton.Background = Avalonia.Media.Brush.Parse("#1C5E8E");

                InstallButton.Content = "CONFIRM INSTALL (WIPE DISK)!";
                InstallButton.Background = Avalonia.Media.Brush.Parse("#FF1F1F");
                StatusLabel.Text = "WARNING: ALL DATA ON THE DEVICE WILL BE WIPED! Click again to confirm.";
                WriteLog($"\n[WARNING] You are about to install Ventoy on {disk.Path}. This will destroy all partitions and data. If you are sure, click the 'CONFIRM INSTALL' button to proceed.\n");
            }
            else
            {
                ResetConfirmations();
                ExecuteOperationAsync(disk, isInstall: true);
            }
        }

        private void UpdateButton_Click(object? sender, RoutedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is not PhysicalDisk disk) return;

            if (!_confirmingUpdate)
            {
                _confirmingUpdate = true;
                _confirmingInstall = false;
                InstallButton.Content = "Install";
                InstallButton.Background = Avalonia.Media.Brush.Parse("#8E1C1C");

                UpdateButton.Content = "CONFIRM UPDATE?";
                UpdateButton.Background = Avalonia.Media.Brush.Parse("#1F85FF");
                StatusLabel.Text = "Updating will preserve Partition 1 data files. Click again to confirm.";
                WriteLog($"\n[WARNING] Updating Ventoy on {disk.Path} will rewrite boot sectors and Partition 2. Click 'CONFIRM UPDATE' to proceed.\n");
            }
            else
            {
                ResetConfirmations();
                ExecuteOperationAsync(disk, isInstall: false);
            }
        }

        private async void ExecuteOperationAsync(PhysicalDisk disk, bool isInstall)
        {
            SetUIEnabled(false);
            LogTextBlock.Text = "";
            ActivityProgressBar.Value = 0;
            ActivityProgressBar.IsIndeterminate = true;

            bool isGpt = StyleComboBox.SelectedIndex == 1;
            bool secureBoot = SecureBootCheckBox.IsChecked == true;
            string filesystem = FsComboBox.SelectedIndex == 1 ? "ntfs" : "exfat";

            StatusLabel.Text = isInstall ? $"Installing Ventoy on {disk.SystemName}..." : $"Updating Ventoy on {disk.SystemName}...";

            try
            {
                await Task.Run(() =>
                {
                    if (isInstall)
                    {
                        // Call the public install method of the console project
                        Ventoy2DiskDotNet.Program.ExecuteInstall(disk, isGpt, secureBoot, filesystem);
                    }
                    else
                    {
                        // Call the public update method of the console project
                        Ventoy2DiskDotNet.Program.ExecuteUpdate(disk, secureBoot);
                    }
                });

                StatusLabel.Text = "Success!";
                ActivityProgressBar.IsIndeterminate = false;
                ActivityProgressBar.Value = 100;
                WriteLog("\n==================================================\n");
                WriteLog(" Ventoy installation/update completed successfully!\n");
                WriteLog("==================================================\n");
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Operation failed.";
                ActivityProgressBar.IsIndeterminate = false;
                WriteLog($"\n[ERROR] Operation failed: {ex.Message}\n");
                WriteLog($"{ex.StackTrace}\n");
            }
            finally
            {
                SetUIEnabled(true);
                DeviceSelected();
            }
        }

        private void WriteLog(string message)
        {
            LogTextBlock.Text += message;
            LogScrollViewer.ScrollToEnd();
        }

        // Custom TextBoxWriter to redirect standard Console output to log
        private class TextBoxWriter : TextWriter
        {
            private readonly Action<string> _logCallback;

            public TextBoxWriter(Action<string> logCallback)
            {
                _logCallback = logCallback;
            }

            public override void WriteLine(string? value)
            {
                if (value != null)
                {
                    _logCallback(value + "\n");
                }
            }

            public override void Write(string? value)
            {
                if (value != null)
                {
                    _logCallback(value);
                }
            }

            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}