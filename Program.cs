using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RazerBatteryTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--probe")
        {
            Probe();
            return;
        }

        using var mutex = new Mutex(true, "RazerBatteryTray.SingleInstance", out bool first);
        if (!first) return;

        RegisterStartupOnce();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }

    /// <summary>Writes every Razer interface and every protocol attempt to a text file and opens it.</summary>
    static void Probe()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"RazerBatteryTray probe {DateTime.Now:u}");
        report.AppendLine($"Windows {Environment.OSVersion.Version}");
        report.AppendLine();
        try
        {
            var r = RazerHid.Read(line => report.AppendLine(line));
            report.AppendLine();
            report.AppendLine($"Result: {r.Status} {r.DeviceName} {r.Percent}% charging={r.Charging}");
        }
        catch (Exception e)
        {
            report.AppendLine(e.ToString());
        }
        var file = Path.Combine(Path.GetTempPath(), "RazerBatteryTray-probe.txt");
        File.WriteAllText(file, report.ToString());
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>
    /// Adds the startup entry on first launch only, so removing it (or disabling it in
    /// Task Manager) sticks. Debug builds never register.
    /// </summary>
    static void RegisterStartupOnce()
    {
#if !DEBUG
        using var app = Registry.CurrentUser.CreateSubKey(@"Software\RazerBatteryTray");
        if (app.GetValue("StartupRegistered") != null) return;
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        run.SetValue("RazerBatteryTray", $"\"{Environment.ProcessPath}\"");
        app.SetValue("StartupRegistered", 1, RegistryValueKind.DWord);
#endif
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    readonly ToolStripMenuItem _status = new() { Enabled = false };

    BatteryReading _reading = new(ReadStatus.NotFound);
    BatteryReading? _lastGood;
    bool _busy;
    Icon? _icon;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, (_, _) => Refresh());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Refresh(); };
        _tray.Visible = true;

        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnThemeChanged;

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        UpdateIcon();
        Refresh();
    }

    void OnThemeChanged(object? sender, EventArgs e) => UpdateIcon();

    async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _reading = await Task.Run(() => RazerHid.Read());
            if (_reading.Status == ReadStatus.Ok) _lastGood = _reading;
        }
        catch (Exception)
        {
            // A failed read must never take the app down; show the unknown state and retry next tick.
            _reading = new BatteryReading(ReadStatus.NotFound);
        }
        finally { _busy = false; }
        UpdateIcon();
    }

    void UpdateIcon()
    {
        string tip, label;

        switch (_reading.Status)
        {
            case ReadStatus.Ok:
                tip = $"{_reading.DeviceName ?? "Razer mouse"}\n{_reading.Percent}%{(_reading.Charging ? " (charging)" : "")}";
                label = _reading.Percent.ToString();
                break;
            case ReadStatus.Asleep:
                tip = $"{_reading.DeviceName ?? "Razer mouse"}\nAsleep or out of range"
                      + (_lastGood != null ? $" (last {_lastGood.Percent}%)" : "");
                label = "–";
                break;
            default:
                tip = "No Razer mouse found";
                label = "–";
                break;
        }

        var icon = TrayText.Render(label);
        _status.Text = tip.Replace("\n", " — ");
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _tray.Icon = icon;
        _icon?.Dispose();
        _icon = icon;
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        SystemEvents.DisplaySettingsChanged -= OnThemeChanged;
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>
/// Draws text in the tray icon the way the taskbar draws its own "ENG" input indicator:
/// Segoe UI Variable (Small optical size) at 12 epx, in the taskbar's primary text colour.
/// </summary>
static class TrayText
{
    const string FontName = "Segoe UI Variable Small";
    const float FontSizeEpx = 12f;
    // Measured on the taskbar: the cap top of "ENG" is 6 px into the 28 px icon slot at 175%.
    const float CapTopEpx = 6f / 1.75f;

    public static Icon Render(string text)
    {
        float scale = DpiScale();
        int size = (int)Math.Round(16 * scale);
        float em = FontSizeEpx * scale;

        using var family = new FontFamily(FontName);
        using var path = new GraphicsPath();

        // Lay glyphs out one by one so the spacing can be tightened when "100" would not fit the slot.
        float x = 0;
        var glyphs = new List<GraphicsPath>();
        foreach (var ch in text)
        {
            var gp = new GraphicsPath();
            gp.AddString(ch.ToString(), family, (int)FontStyle.Regular, em, new PointF(x, 0), StringFormat.GenericTypographic);
            glyphs.Add(gp);
            x += Advance(family, em, ch);
        }
        float ink = Bounds(glyphs).Width;
        float squeeze = glyphs.Count > 1 && ink > size - 1 ? (ink - (size - 1)) / (glyphs.Count - 1) : 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            using var m = new Matrix();
            m.Translate(-squeeze * i, 0);
            glyphs[i].Transform(m);
            path.AddPath(glyphs[i], false);
            glyphs[i].Dispose();
        }

        // Centre horizontally; put the cap top where the taskbar puts it.
        var b = path.GetBounds();
        float capTop = CapTopEpx * scale;
        using (var m = new Matrix())
        {
            m.Translate(MathF.Round((size - b.Width) / 2f - b.X), MathF.Round(capTop - CapTop(family, em)));
            path.Transform(m);
        }

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.FillPath(Brushes.White, path);
        }
        ApplyCoverage(bmp, TextColor());
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    /// <summary>
    /// The taskbar renders text with DirectWrite, which boosts the contrast of anti-aliased edges;
    /// GDI+ does not. coverage^0.4 was fitted against the taskbar's own "ENG" pixels.
    /// </summary>
    static void ApplyCoverage(Bitmap bmp, Color color)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, bmp.PixelFormat);
        var px = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        for (int i = 0; i < px.Length; i += 4)
        {
            double coverage = px[i + 3] / 255.0;
            px[i] = color.B;
            px[i + 1] = color.G;
            px[i + 2] = color.R;
            px[i + 3] = (byte)Math.Round(color.A * Math.Pow(coverage, 0.4));
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    static float Advance(FontFamily family, float em, char ch)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font(family, em, FontStyle.Regular, GraphicsUnit.Pixel);
        return g.MeasureString(ch.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    // Top of a capital/figure relative to the layout origin used by AddString.
    static float CapTop(FontFamily family, float em)
    {
        using var gp = new GraphicsPath();
        gp.AddString("0", family, (int)FontStyle.Regular, em, PointF.Empty, StringFormat.GenericTypographic);
        return gp.GetBounds().Y;
    }

    static RectangleF Bounds(List<GraphicsPath> paths)
    {
        var r = paths[0].GetBounds();
        foreach (var p in paths.Skip(1)) r = RectangleF.Union(r, p.GetBounds());
        return r;
    }

    // TextFillColorPrimary — the colour of the taskbar's own text.
    static Color TextColor() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            ?.GetValue("SystemUsesLightTheme") as int? == 1 ? Color.FromArgb(0xE4, 0, 0, 0) : Color.White;

    // The icon is shown on the taskbar, so use the taskbar's current DPI rather than the
    // system DPI captured at startup (which goes stale when scaling changes).
    static float DpiScale()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) dpi = GetDpiForSystem();
        return dpi / 96f;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
}
