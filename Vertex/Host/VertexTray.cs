using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Vertex.Host;

/// <summary>What Vertex is doing, for the tray dot.</summary>
public enum VertexActivity { Off, Starting, Listening, Thinking, Speaking }

/// <summary>
/// Vertex in the notification area: a status dot (grey off, blue starting, green listening, amber thinking, cyan
/// speaking) and a menu to open the control panel in the browser, start or leave, start with Windows, and quit.
/// The control panel (Blazor Server at <see cref="ControlPanelUrl"/>) is where everything else lives.
/// </summary>
/// <remarks>
/// Ported from SpawnDev.Reachy's RoseTray, which Aubs has used since July: the Run-key autostart, the Windows 11
/// "promote out of the overflow" fix, and the drawn dot icons are the same proven code.
/// </remarks>
public sealed class VertexTray : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Vertex";
    public const string ControlPanelUrl = "http://localhost:5280";

    private readonly VertexHost _host;
    private readonly Action _quit;
    private readonly NotifyIcon _icon;
    private readonly Dictionary<VertexActivity, Icon> _icons = [];
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private VertexActivity _shown = (VertexActivity)(-1);
    private bool _busy;

    /// <param name="quit">Stops the control panel's web host; the tray then ends its own thread.</param>
    public VertexTray(VertexHost host, Action quit)
    {
        _host = host;
        _quit = quit;
        foreach (var a in Enum.GetValues<VertexActivity>()) _icons[a] = MakeDotIcon(ColorFor(a));
        _icon = new NotifyIcon { Icon = _icons[VertexActivity.Off], Text = "Vertex - off", Visible = true };
        _icon.DoubleClick += (_, _) => OpenControlPanel();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _icon.ContextMenuStrip?.Show(Cursor.Position); };
        BuildMenu();
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        // Windows 11 drops new tray icons into the hidden overflow; promote ours so the dot is visible. Explorer
        // writes the icon's registry entry a moment after it first appears, so try again shortly.
        PromoteTrayIcon();
        var promote = new System.Windows.Forms.Timer { Interval = 3000 };
        promote.Tick += (_, _) => { promote.Stop(); promote.Dispose(); PromoteTrayIcon(); };
        promote.Start();
    }

    /// <summary>Opens the control panel in the system browser.</summary>
    public static void OpenControlPanel()
    {
        try { Process.Start(new ProcessStartInfo(ControlPanelUrl) { UseShellExecute = true }); } catch { }
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Vertex - off") { Enabled = false, Name = "header" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open control panel", null, (_, _) => OpenControlPanel()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Start", null, (_, _) => StartOrLeave()) { Name = "startstop" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Name = "autostart", Checked = IsAutostart() });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
        menu.Opening += (_, _) => Refresh();
        _icon.ContextMenuStrip = menu;
    }

    private void Refresh()
    {
        var activity = _host.Activity;
        var status = _host.Status;
        if (activity != _shown)
        {
            _icon.Icon = _icons[activity];
            _shown = activity;
        }
        var label = activity switch
        {
            VertexActivity.Off => "off",
            VertexActivity.Starting => "starting",
            VertexActivity.Listening => "listening",
            VertexActivity.Thinking => "thinking",
            VertexActivity.Speaking => "talking",
            _ => activity.ToString(),
        };
        var text = $"Vertex ({_host.Settings.Character}) - {label}";
        _icon.Text = text.Length <= 63 ? text : text[..63]; // NotifyIcon.Text is limited to 63 characters
        var menu = _icon.ContextMenuStrip!;
        ((ToolStripMenuItem)menu.Items["header"]!).Text = status;
        var startStop = (ToolStripMenuItem)menu.Items["startstop"]!;
        startStop.Text = _host.State == HostState.Idle ? "Start" : "Leave";
        startStop.Enabled = !_busy && _host.State is HostState.Idle or HostState.Playing;
        ((ToolStripMenuItem)menu.Items["autostart"]!).Checked = IsAutostart();
    }

    private async void StartOrLeave()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_host.State == HostState.Idle)
            {
                await _host.StartAsync();
                // A Microsoft sign-in waits for its device code on the control panel.
                if (_host.SignInPrompt != null) OpenControlPanel();
            }
            else await _host.StopAsync();
        }
        catch (Exception e)
        {
            _icon.ShowBalloonTip(6000, "Vertex", Short(e.Message), ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void Quit()
    {
        _poll.Stop();
        _icon.Visible = false;
        try { await _host.StopAsync(); } catch { /* leaving on the way out */ }
        _quit();
        foreach (var i in _icons.Values) i.Dispose();
        _icon.Dispose();
        ExitThread();
    }

    // ---- start with Windows (HKCU Run key) ----------------------------------

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostart()) key.DeleteValue(RunValue, throwOnMissingValue: false);
        // --background: at login the icon appears quietly; nothing opens until someone asks.
        else key.SetValue(RunValue, $"\"{ExePath()}\" --background");
        Refresh();
    }

    // ---- helpers ------------------------------------------------------------

    private static string ExePath() => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static void PromoteTrayIcon()
    {
        try
        {
            var exe = ExePath();
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name, writable: true);
                if (sub?.GetValue("ExecutablePath") is string p && string.Equals(p, exe, StringComparison.OrdinalIgnoreCase))
                    sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
        }
        catch { /* cosmetic: never fail startup over the tray-visibility flag */ }
    }

    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + "...";

    private static Color ColorFor(VertexActivity a) => a switch
    {
        VertexActivity.Off => Color.Gray,
        VertexActivity.Starting => Color.DodgerBlue,
        VertexActivity.Listening => Color.LimeGreen,
        VertexActivity.Thinking => Color.Orange,
        VertexActivity.Speaking => Color.DeepSkyBlue,
        _ => Color.Gray,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A small filled circle in the given colour, for the tray dot.</summary>
    private static Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(90, Color.Black), 2);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        var hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }
        finally { DestroyIcon(hicon); }
    }
}
