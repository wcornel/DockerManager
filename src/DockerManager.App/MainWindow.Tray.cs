using System.Windows;
using DockerManager.App.Helpers;
using DockerManager.App.Models;
using DockerManager.App.Services;
using DockerManager.App.ViewModels;
using DockerManager.App.Views;

namespace DockerManager.App;

public partial class MainWindow
{
    private System.Drawing.Font? _trayBoldFont;
    private System.Drawing.Icon? _ownedTrayIcon;
    private bool _trayInitialized;
    private bool _trayDisposed;

    private void InitializeTrayIcon()
    {
        try
        {
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/DockerManager.App;component/app.ico"))?.Stream;
            System.Drawing.Icon icon;
            if (iconStream != null)
            {
                _ownedTrayIcon = new System.Drawing.Icon(iconStream);
                icon = _ownedTrayIcon;
            }
            else
            {
                icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "DockerManager — Profile & Container Hub",
                Visible = true
            };

            BuildTrayContextMenu();
            LocalizationService.Instance.LanguageChanged += BuildTrayContextMenu;
            _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
            _trayInitialized = true;
        }
        catch
        {
            _trayInitialized = false;
        }
    }

    private void BuildTrayContextMenu()
    {
        if (_notifyIcon == null || _trayDisposed) return;

        var oldMenu = _notifyIcon.ContextMenuStrip;
        var contextMenu = new System.Windows.Forms.ContextMenuStrip();

        PopulateTrayContextMenu(contextMenu);

        contextMenu.Opening += (_, _) =>
        {
            PopulateTrayContextMenu(contextMenu);
        };

        _notifyIcon.ContextMenuStrip = contextMenu;

        if (oldMenu != null)
        {
            DisposeToolStripItems(oldMenu.Items);
            oldMenu.Dispose();
        }

        UpdateTrayTooltip();
    }

    private void PopulateTrayContextMenu(System.Windows.Forms.ContextMenuStrip contextMenu)
    {
        if (_notifyIcon == null || _trayDisposed) return;
        var loc = LocalizationService.Instance;
        DisposeToolStripItems(contextMenu.Items);

        _trayBoldFont ??= new System.Drawing.Font(contextMenu.Font, System.Drawing.FontStyle.Bold);

        var openItem = new System.Windows.Forms.ToolStripMenuItem(loc.Get("Tray_Open"), null, (_, _) => RestoreWindow());
        openItem.Font = _trayBoldFont;
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var currentServer = _viewModel.SelectedServer;
        var currentServerName = currentServer?.Name ?? loc.Get("Tray_Local");

        // Server menu item with dropdown
        var serverMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Server")}: {currentServerName}");
        serverMenuItem.Font = _trayBoldFont;

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
        var hasProfile = currentProfile != null && !string.IsNullOrWhiteSpace(currentProfile.Name);
        var currentProfileName = hasProfile ? currentProfile!.Name : loc.Get("Tray_NoProfile");

        var profileMenuItem = new System.Windows.Forms.ToolStripMenuItem($"{loc.Get("Tray_Profile")}: {currentProfileName}");
        profileMenuItem.Font = _trayBoldFont;

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

        var startText = hasProfile ? $"{loc.Get("Tray_StartAll")} ({currentProfile!.Name})" : loc.Get("Tray_StartAll");
        var stopText = hasProfile ? $"{loc.Get("Tray_StopAll")} ({currentProfile!.Name})" : loc.Get("Tray_StopAll");
        var updateText = hasProfile ? $"{loc.Get("Tray_UpdateAll")} ({currentProfile!.Name})" : loc.Get("Tray_UpdateAll");
        var backupText = hasProfile ? $"{loc.Get("Tray_BackupVolumes")} ({currentProfile!.Name})" : loc.Get("Tray_BackupVolumes");

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
            DisposeTrayIcon();
            Application.Current.Shutdown();
        }));

        UpdateTrayTooltip();
    }

    private static void DisposeToolStripItems(System.Windows.Forms.ToolStripItemCollection items)
    {
        var copy = new System.Windows.Forms.ToolStripItem[items.Count];
        items.CopyTo(copy, 0);
        items.Clear();

        foreach (var item in copy)
        {
            if (item is System.Windows.Forms.ToolStripDropDownItem ddItem && ddItem.HasDropDownItems)
            {
                DisposeToolStripItems(ddItem.DropDownItems);
            }
            try { item.Dispose(); } catch { }
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayDisposed) return;
        _trayDisposed = true;

        LocalizationService.Instance.LanguageChanged -= BuildTrayContextMenu;

        if (_notifyIcon != null)
        {
            try
            {
                var menu = _notifyIcon.ContextMenuStrip;
                _notifyIcon.ContextMenuStrip = null;
                if (menu != null)
                {
                    DisposeToolStripItems(menu.Items);
                    menu.Dispose();
                }
            }
            catch { }

            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
            _notifyIcon = null;
        }

        try { _ownedTrayIcon?.Dispose(); } catch { }
        _ownedTrayIcon = null;

        try { _trayBoldFont?.Dispose(); } catch { }
        _trayBoldFont = null;
    }

    private void UpdateTrayTooltip()
    {
        if (_notifyIcon == null || _trayDisposed) return;
        var loc = LocalizationService.Instance;
        var server = _viewModel.SelectedServer?.Name ?? "Docker";
        var profile = !string.IsNullOrWhiteSpace(_viewModel.SelectedProfile?.Name)
            ? _viewModel.SelectedProfile.Name
            : loc.Get("Tray_NoProfile");
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
        if (WindowState == WindowState.Minimized && _trayInitialized && _notifyIcon != null && !_trayDisposed)
        {
            Hide();
            var tip = LocalizationService.Instance.Get("Tray_MinimizedTip");
            _notifyIcon.ShowBalloonTip(1500, "DockerManager", tip, System.Windows.Forms.ToolTipIcon.Info);
        }
    }

}
