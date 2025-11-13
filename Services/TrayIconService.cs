using System;
using System.Windows;
using System.Windows.Forms;

namespace SideHUD.Services;

public class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private bool _clickThrough = false;
    private bool _alwaysOnTop = true;
    private bool _startupEnabled = false;
    private Window? _mainWindow;

    public event EventHandler? ToggleClickThrough;
    public event EventHandler? ToggleAlwaysOnTop;
    public event EventHandler? ToggleStartup;
    public event EventHandler? ResampleWallpaper;
    public event EventHandler? ReloadSensors;
    public event EventHandler? ShowDebugInfo;
    public event EventHandler? Exit;

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            UpdateMenu();
        }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            _alwaysOnTop = value;
            UpdateMenu();
        }
    }

    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;
        
        // Check current startup status
        _startupEnabled = StartupService.IsStartupEnabled();
        
        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "SideHUD - Right-click for menu, Double-click to exit",
            Visible = true
        };

        // Double-click to exit
        _notifyIcon.DoubleClick += (s, e) => Exit?.Invoke(this, EventArgs.Empty);

        UpdateMenu();
    }
    
    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            _startupEnabled = value;
            UpdateMenu();
        }
    }

    private void UpdateMenu()
    {
        if (_notifyIcon == null)
            return;

        var menu = new ContextMenuStrip();
        
        menu.Items.Add(new ToolStripMenuItem(
            _clickThrough ? "Disable Click-through" : "Enable Click-through",
            null,
            (s, e) => ToggleClickThrough?.Invoke(this, EventArgs.Empty)
        ));

        menu.Items.Add(new ToolStripMenuItem(
            _alwaysOnTop ? "Disable Always on Top" : "Enable Always on Top",
            null,
            (s, e) => ToggleAlwaysOnTop?.Invoke(this, EventArgs.Empty)
        ));

        menu.Items.Add(new ToolStripSeparator());
        
        menu.Items.Add(new ToolStripMenuItem(
            _startupEnabled ? "Disable Startup with Windows" : "Enable Startup with Windows",
            null,
            (s, e) => ToggleStartup?.Invoke(this, EventArgs.Empty)
        ));
        
        menu.Items.Add(new ToolStripSeparator());
        
        menu.Items.Add(new ToolStripMenuItem(
            "Re-sample Wallpaper Colors",
            null,
            (s, e) => ResampleWallpaper?.Invoke(this, EventArgs.Empty)
        ));

        menu.Items.Add(new ToolStripMenuItem(
            "Reload Sensors",
            null,
            (s, e) => ReloadSensors?.Invoke(this, EventArgs.Empty)
        ));

        menu.Items.Add(new ToolStripMenuItem(
            "Debug: List All Sensors",
            null,
            (s, e) => ShowDebugInfo?.Invoke(this, EventArgs.Empty)
        ));

        menu.Items.Add(new ToolStripSeparator());
        
        menu.Items.Add(new ToolStripMenuItem(
            "Exit",
            null,
            (s, e) => Exit?.Invoke(this, EventArgs.Empty)
        ));

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
    }
}

