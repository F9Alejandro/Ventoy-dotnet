using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ventoy2DiskDotNet;
using DiskInfo = Ventoy2DiskDotNet.Program.DiskInfo;

namespace Ventoy2DiskAvalonia;

public class DiskComboBoxItem
{
    public DiskInfo Disk { get; }
    public string Description => $"{Disk.Name} - {Disk.Model} ({Disk.HumanSize})";

    public DiskComboBoxItem(DiskInfo disk)
    {
        Disk = disk;
    }
}

public partial class MainWindow : Window
{
    private List<DiskInfo> _disks = new List<DiskInfo>();
    private bool _isOperating = false;
    private string _pendingOperation = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        PackageVersionText.Text = Ventoy2DiskDotNet.Program.GetVentoyVersion();
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        try
        {
            _disks = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Ventoy2DiskDotNet.Program.GetWindowsDisks(true)
                : Ventoy2DiskDotNet.Program.GetLinuxDisks(true);

            DeviceComboBox.ItemsSource = _disks.Select(d => new DiskComboBoxItem(d)).ToList();
            if (_disks.Count > 0)
            {
                DeviceComboBox.SelectedIndex = 0;
            }
            else
            {
                DeviceComboBox.SelectedIndex = -1;
                UpdateDeviceLabels(null);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error scanning devices: {ex.Message}";
        }
    }

    private void UpdateDeviceLabels(DiskInfo? disk)
    {
        if (disk == null)
        {
            DeviceVersionText.Text = "None";
            DeviceStyleText.Text = "MBR";
            DeviceSecureText.Text = "No";
        }
        else
        {
            DeviceVersionText.Text = string.IsNullOrEmpty(disk.VtoyVer) ? "None" : disk.VtoyVer;
            DeviceStyleText.Text = disk.VtoyPartStyle == 1 ? "GPT" : "MBR";
            DeviceSecureText.Text = disk.VtoySecureBoot == 1 ? "Yes" : "No";
        }
    }

    private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is DiskComboBoxItem item)
        {
            UpdateDeviceLabels(item.Disk);
        }
        else
        {
            UpdateDeviceLabels(null);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshDevices();
        StatusText.Text = "Device list refreshed.";
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        // Mutual exclusion for MBR/GPT Menu RadioItems
        if (sender == MenuMBR && MenuMBR.IsChecked)
        {
            MenuGPT.IsChecked = false;
        }
        else if (sender == MenuGPT && MenuGPT.IsChecked)
        {
            MenuMBR.IsChecked = false;
        }
    }

    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        PromptOperation("install");
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        PromptOperation("update");
    }

    private void OnClearVentoy(object? sender, RoutedEventArgs e)
    {
        PromptOperation("clean");
    }

    private void PromptOperation(string operationType)
    {
        if (_isOperating) return;

        if (DeviceComboBox.SelectedItem is not DiskComboBoxItem selectedItem)
        {
            StatusText.Text = "Please select a target device first.";
            return;
        }

        _pendingOperation = operationType;
        if (operationType == "install")
        {
            ModalMessageText.Text = $"WARNING: All data on {selectedItem.Description} will be lost!\nAre you sure you want to install Ventoy?";
        }
        else if (operationType == "clean")
        {
            ModalMessageText.Text = $"WARNING: All partition info on {selectedItem.Description} will be wiped!\nAre you sure you want to clear Ventoy?";
        }
        else if (operationType == "update")
        {
            ModalMessageText.Text = $"Are you sure you want to update Ventoy on {selectedItem.Description}?\n(Your user files will not be touched.)";
        }

        ModalOverlay.IsVisible = true;
    }

    private void OnModalNoClick(object? sender, RoutedEventArgs e)
    {
        ModalOverlay.IsVisible = false;
        _pendingOperation = string.Empty;
    }

    private async void OnModalYesClick(object? sender, RoutedEventArgs e)
    {
        ModalOverlay.IsVisible = false;
        if (string.IsNullOrEmpty(_pendingOperation)) return;

        string op = _pendingOperation;
        _pendingOperation = string.Empty;

        if (DeviceComboBox.SelectedItem is not DiskComboBoxItem selectedItem) return;

        string diskName = selectedItem.Disk.Name;
        int style = MenuGPT.IsChecked ? 1 : 0;
        int secureBoot = MenuSecureBoot.IsChecked ? 1 : 0;

        SetUiEnabled(false);
        _isOperating = true;
        StatusText.Text = $"Starting {op} on {diskName}...";
        OpProgressBar.Value = 0;

        // Run the background operation
        Ventoy2DiskDotNet.Program.StartBackgroundOperation(op, diskName, style, secureBoot, "0", "exfat");

        // Polling loop for progress updates
        await Task.Run(async () =>
        {
            int lastPercent = -1;
            while (true)
            {
                int percent = Ventoy2DiskDotNet.Program.Percent;
                string result = Ventoy2DiskDotNet.Program.ProcessResult;

                if (percent != lastPercent)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        OpProgressBar.Value = percent;
                        StatusText.Text = $"Running: {percent}%";
                    });
                    lastPercent = percent;
                }

                if (percent >= 100)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText.Text = result == "success"
                            ? $"{op} completed successfully!"
                            : $"{op} failed. Check console logs for details.";
                        
                        SetUiEnabled(true);
                        _isOperating = false;
                        RefreshDevices();
                    });
                    break;
                }

                await Task.Delay(200);
            }
        });
    }

    private void SetUiEnabled(bool enabled)
    {
        DeviceComboBox.IsEnabled = enabled;
        InstallButton.IsEnabled = enabled;
        UpdateButton.IsEnabled = enabled;
        MenuSecureBoot.IsEnabled = enabled;
        MenuMBR.IsEnabled = enabled;
        MenuGPT.IsEnabled = enabled;
    }

    // ==========================================
    // VLNK TAB EVENT HANDLERS
    // ==========================================

    private async void OnVlnkSourceBrowseClick(object? sender, RoutedEventArgs e)
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Source Image File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Ventoy Supported Images")
                {
                    Patterns = new[] { "*.iso", "*.img", "*.wim", "*.efi", "*.vhd", "*.vhdx", "*.dat", "*.vtoy" }
                }
            }
        });

        if (files != null && files.Count > 0)
        {
            string path = files[0].Path.LocalPath;
            VlnkSourceTextBox.Text = path;
            
            // Set default output destination
            string ext = Path.GetExtension(path);
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(path);
            VlnkDestTextBox.Text = Path.Combine(dir, $"{fileName}.vlnk{ext.ToLowerInvariant()}");

            // Trigger auto-resolve automatically for convenience
            ResolvePath(path);
        }
    }

    private async void OnVlnkDestBrowseClick(object? sender, RoutedEventArgs e)
    {
        string currentSource = VlnkSourceTextBox.Text ?? string.Empty;
        string defaultName = "";
        string defaultExt = ".iso";
        if (!string.IsNullOrEmpty(currentSource))
        {
            string ext = Path.GetExtension(currentSource);
            string fileName = Path.GetFileNameWithoutExtension(currentSource);
            defaultName = $"{fileName}.vlnk{ext.ToLowerInvariant()}";
            defaultExt = $"*{ext.ToLowerInvariant()}";
        }

        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Virtual Link File",
            DefaultExtension = defaultExt,
            SuggestedFileName = defaultName
        });

        if (file != null)
        {
            VlnkDestTextBox.Text = file.Path.LocalPath;
        }
    }

    private void OnVlnkResolveClick(object? sender, RoutedEventArgs e)
    {
        string path = VlnkSourceTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            VlnkStatusText.Text = "Please select a source image file first.";
            return;
        }
        ResolvePath(path);
    }

    private void ResolvePath(string path)
    {
        try
        {
            VlnkStatusText.Text = "Resolving path...";
            string diskDevice = string.Empty;
            ulong partOffsetBytes = 0;
            string relPath = string.Empty;
            bool success = false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                success = VentoyVlnk.Program.ResolveWindowsPath(path, out diskDevice, out partOffsetBytes, out relPath);
            }
            else
            {
                success = VentoyVlnk.Program.ResolveLinuxPath(path, out diskDevice, out partOffsetBytes, out relPath);
            }

            if (success)
            {
                uint sig = 0;
                bool gotSig = VentoyVlnk.Program.GetDiskSignature(diskDevice, out sig);

                VlnkResolvedDiskText.Text = diskDevice;
                VlnkResolvedOffsetText.Text = $"{partOffsetBytes} bytes (Sector {partOffsetBytes / 512})";
                VlnkResolvedSigText.Text = gotSig ? $"0x{sig:X8}" : "Failed to read signature";

                // Populate manual fields as pre-fills in case they switch to manual
                VlnkManualDiskTextBox.Text = diskDevice;
                VlnkManualOffsetTextBox.Text = (partOffsetBytes / 512).ToString();
                VlnkManualSigTextBox.Text = gotSig ? sig.ToString("X8") : "";
                VlnkManualRelPathTextBox.Text = relPath;

                VlnkStatusText.Text = "Path successfully resolved!";
            }
            else
            {
                VlnkResolvedDiskText.Text = "Failed to resolve";
                VlnkResolvedOffsetText.Text = "Failed to resolve";
                VlnkResolvedSigText.Text = "Failed to resolve";
                VlnkStatusText.Text = "Auto-resolution failed. Run as root/administrator, or use manual override.";
            }
        }
        catch (Exception ex)
        {
            VlnkStatusText.Text = $"Error resolving path: {ex.Message}";
        }
    }

    private void OnVlnkManualCheckChanged(object? sender, RoutedEventArgs e)
    {
        bool isManual = VlnkManualOverrideCheck.IsChecked ?? false;
        VlnkManualInputs.IsVisible = isManual;
    }

    private void OnGenerateVlnkClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string sourcePath = VlnkSourceTextBox.Text ?? string.Empty;
            string destPath = VlnkDestTextBox.Text ?? string.Empty;

            if (string.IsNullOrEmpty(sourcePath))
            {
                VlnkStatusText.Text = "Please specify a source image file.";
                return;
            }

            if (string.IsNullOrEmpty(destPath))
            {
                VlnkStatusText.Text = "Please specify a destination .vlnk path.";
                return;
            }

            if (!VentoyVlnk.Program.IsSupportedImgSuffix(sourcePath))
            {
                VlnkStatusText.Text = "Error: Unsupported image file format.";
                return;
            }

            string diskDevice = string.Empty;
            ulong partOffsetBytes = 0;
            string relPath = string.Empty;
            uint sig = 0;

            bool isManual = VlnkManualOverrideCheck.IsChecked ?? false;
            if (isManual)
            {
                diskDevice = VlnkManualDiskTextBox.Text ?? string.Empty;
                string offsetStr = VlnkManualOffsetTextBox.Text ?? string.Empty;
                string sigStr = VlnkManualSigTextBox.Text ?? string.Empty;
                relPath = VlnkManualRelPathTextBox.Text ?? string.Empty;

                if (string.IsNullOrEmpty(diskDevice) || string.IsNullOrEmpty(offsetStr) || string.IsNullOrEmpty(sigStr) || string.IsNullOrEmpty(relPath))
                {
                    VlnkStatusText.Text = "Please fill in all manual override inputs.";
                    return;
                }

                if (!ulong.TryParse(offsetStr, out ulong sectors))
                {
                    VlnkStatusText.Text = "Invalid partition start sector.";
                    return;
                }
                partOffsetBytes = sectors * 512;

                try
                {
                    sig = Convert.ToUInt32(sigStr.Replace("0x", ""), 16);
                }
                catch
                {
                    VlnkStatusText.Text = "Invalid MBR signature hex value.";
                    return;
                }
            }
            else
            {
                // Auto-resolve
                bool resolved = false;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    resolved = VentoyVlnk.Program.ResolveWindowsPath(sourcePath, out diskDevice, out partOffsetBytes, out relPath);
                }
                else
                {
                    resolved = VentoyVlnk.Program.ResolveLinuxPath(sourcePath, out diskDevice, out partOffsetBytes, out relPath);
                }

                if (!resolved)
                {
                    VlnkStatusText.Text = "Auto-resolution failed. Use Manual Override.";
                    return;
                }

                if (!VentoyVlnk.Program.GetDiskSignature(diskDevice, out sig))
                {
                    VlnkStatusText.Text = "Failed to read target disk signature.";
                    return;
                }
            }

            // Perform serialization
            byte[] fileData = VentoyVlnk.Program.Serialize(sig, partOffsetBytes, relPath);
            File.WriteAllBytes(destPath, fileData);

            VlnkStatusText.Text = $"Virtual Link created successfully at '{Path.GetFileName(destPath)}'!";
        }
        catch (Exception ex)
        {
            VlnkStatusText.Text = $"Error generating vlnk: {ex.Message}";
        }
    }
}