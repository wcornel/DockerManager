using System.Windows;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;

namespace DockerManager.App;

public partial class MainWindow
{
    private void InitializeTrayIcon()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/DockerManager.App;component/app.ico"))?.Stream;
            var icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application;

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "DockerManager — Profile & Container Hub",
                Visible = true
            };

            BuildTrayContextMenu();
            LocalizationService.Instance.LanguageChanged += BuildTrayContextMenu;
            _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
        }
        catch { }
    }

    private void BuildTrayContextMenu()
    {
        if (_notifyIcon == null) return;
        var contextMenu = new System.Windows.Forms.ContextMenuStrip();

        PopulateTrayContextMenu(contextMenu);

        contextMenu.Opening += (_, _) =>
        {
            PopulateTrayContextMenu(contextMenu);
        };

        _notifyIcon.ContextMenuStrip = contextMenu;
        UpdateTrayTooltip();
    }

    private void PopulateTrayContextMenu(System.Windows.Forms.ContextMenuStrip contextMenu)
    {
        if (_notifyIcon == null) return;
        var loc = LocalizationService.Instance;
        contextMenu.Items.Clear();

        var openItem = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_Open"), null, (_, _) => RestoreWindow());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var currentServer = _viewModel.SelectedServer;
        var currentServerName = currentServer?.Name ?? "Lokaal";

        // Server menu item with dropdown
        var serverMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Server")}: {currentServerName}");
        serverMenuItem.Font = new System.Drawing.Font(serverMenuItem.Font, System.Drawing.FontStyle.Bold);

        foreach (var server in _viewModel.Servers)
        {
            var isCurrent = currentServer != null && server.Id.Equals(currentServer.Id, StringComparison.OrdinalIgnoreCase);
            var item = new System.Windows.Forms.ToolStripMenuItem((isCurrent ? "✓ " : "    ") + server.Name, null, (_, _) =>
            {
                if (!isCurrent)
                {
                    _viewModel.SelectedServer = server;
                    UpdateTrayTooltip();
                    _notifyIcon?.ShowBalloonTip(1500, "DockerManager", string.Format(loc.Get("Tray_SwitchedServer"), server.Name), System.Windows.Forms.ToolTipIcon.Info);
                }
            })
            {
                Checked = isCurrent
            };
            serverMenuItem.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(serverMenuItem);

        // Profile menu item with dropdown
        var currentProfile = _viewModel.SelectedProfile;
        var currentProfileName = currentProfile?.Name ?? "(Geen profiel)";

        var profileMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Profile")}: {currentProfileName}");
        profileMenuItem.Font = new System.Drawing.Font(profileMenuItem.Font, System.Drawing.FontStyle.Bold);

        foreach (var profile in _viewModel.Profiles)
        {
            var isCurrent = currentProfile != null && profile.Name.Equals(currentProfile.Name, StringComparison.OrdinalIgnoreCase);
            var item = new System.Windows.Forms.ToolStripMenuItem((isCurrent ? "✓ " : "    ") + profile.Name, null, (_, _) =>
            {
                if (!isCurrent)
                {
                    _viewModel.SelectedProfile = profile;
                    UpdateTrayTooltip();
                    _notifyIcon?.ShowBalloonTip(1500, "DockerManager", string.Format(loc.Get("Tray_SwitchedProfile"), profile.Name), System.Windows.Forms.ToolTipIcon.Info);
                }
            })
            {
                Checked = isCurrent
            };
            profileMenuItem.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(profileMenuItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var startText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_StartAll") : $"{loc.Get("Tray_StartAll")} ({currentProfileName})";
        var stopText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_StopAll") : $"{loc.Get("Tray_StopAll")} ({currentProfileName})";
        var updateText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_UpdateAll") : $"{loc.Get("Tray_UpdateAll")} ({currentProfileName})";
        var backupText = string.IsNullOrEmpty(currentProfileName) ? loc.Get("Tray_BackupVolumes") : $"{loc.Get("Tray_BackupVolumes")} ({currentProfileName})";

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(startText, null, (_, _) => _ = _viewModel.StartAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(stopText, null, (_, _) => _ = _viewModel.StopAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(updateText, null, (_, _) => _ = _viewModel.UpdateAllCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(backupText, null, (_, _) => _ = _viewModel.BackupVolumesCommand.ExecuteAsync(null)));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_CheckPorts"), null, (_, _) =>
        {
            RestoreWindow();
            _viewModel.CheckPortConflictsCommand.Execute(null);
        }));

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_Exit"), null, (_, _) =>
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Application.Current.Shutdown();
        }));

        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_notifyIcon == null) return;
        var server = _viewModel.SelectedServer?.Name ?? "Docker";
        var profile = _viewModel.SelectedProfile?.Name ?? "Geen profiel";
        var text = $"DockerManager — {server} ({profile})";
        if (text.Length > 63)
        {
            text = text.Substring(0, 60) + "...";
        }
        _notifyIcon.Text = text;
    }

    public void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            var tip = LocalizationService.Instance.Get("Tray_MinimizedTip");
            _notifyIcon?.ShowBalloonTip(1500, "DockerManager", tip, System.Windows.Forms.ToolTipIcon.Info);
        }
    }

}
