// rzswitch-setup: installer panel (WinForms, Razer-styled, borderless).
// Requires admin (manifest). Installs/removes the rzswitch service and
// toggles Razer GameManagerService3. Own-service start is locked while
// GameManager is active (port/registry conflict).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Svc
{
    public const string Gms = "Razer Game Manager Service 3";
    public const string Own = "rzswitch";

    public record Info(bool Exists, string Status, string StartType);

    public static Info Query(string name)
    {
        try
        {
            if (!ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase)))
                return new Info(false, "-", "-");
            using var sc = new ServiceController(name);
            string status;
            try { status = sc.Status.ToString(); }
            catch { status = "unknown"; }
            return new Info(true, status, StartMode(name));
        }
        catch { return new Info(false, "-", "-"); }
    }

    private static string StartMode(string name)
    {
        var (code, _) = Run("sc", $"qc \"{name}\"");
        foreach (var line in code.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("START_TYPE"))
            {
                var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return parts[^1] switch
                {
                    "AUTO_START" => "auto",
                    "DEMAND_START" => "demand",
                    "DISABLED" => "disabled",
                    var x => x,
                };
            }
        }
        return "?";
    }

    public static (string output, int code) Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return (o.Trim(), p.ExitCode);
        }
        catch (Exception ex) { return (ex.Message, -1); }
    }

    public static string ServiceExe() =>
        Path.Combine(AppContext.BaseDirectory, "rzswitch.exe");
}

static class SysInstall
{
    public const string UninstallId = "rzswitch";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\rzswitch";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rzswitch");

    public static string SvcExeDst => Path.Combine(InstallDir, "rzswitch.exe");
    public static string SetupExeDst => Path.Combine(InstallDir, "rzswitch-setup.exe");

    public static string SetupExeSrc =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "rzswitch-setup.exe");

    public static bool IsInstalled()
    {
        if (File.Exists(SvcExeDst) || File.Exists(SetupExeDst))
            return true;
        using var k = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
        return k != null;
    }

    public static string Install()
    {
        var srcSvc = Svc.ServiceExe();
        var srcSetup = SetupExeSrc;
        if (!File.Exists(srcSvc))
            return $"missing {srcSvc} - keep setup next to rzswitch.exe";
        if (!File.Exists(srcSetup))
            return $"missing {srcSetup}";
        // first: stop the current service; if it is registered from the
        // current folder, unregister it so the system-level service takes over
        var prevBin = GetBinaryPath();
        var prev = Svc.Query(Svc.Own);
        string was = DescribePrevious(prevBin, prev);
        if (prev.Exists)
        {
            Svc.Run("sc", $"stop {Svc.Own}");
            WaitForStop(15000);
            if (!IsSystemBinary(prevBin))
            {
                var (oDel, cDel) = Svc.Run("sc", $"delete {Svc.Own}");
                if (!WaitForDelete(15000))
                    return $"{was} | cannot unregister local service: delete[{cDel}] {First(oDel)}";
                was += " | local service unregistered";
            }
        }
        Directory.CreateDirectory(InstallDir);
        CopyIfDifferent(srcSvc, SvcExeDst);
        CopyIfDifferent(srcSetup, SetupExeDst);
        // re-point service to the AppData copy
        Svc.Run("sc", $"delete {Svc.Own}");
        var (o1, c1) = Svc.Run("sc", $"create {Svc.Own} binPath= \"{SvcExeDst}\" start= auto");
        var (o2, c2) = Svc.Run("sc", $"description {Svc.Own} \"Razer GameManager replacement: foreground detector + SignalR hub\"");
        RegisterUninstall();
        var gms = Svc.Query(Svc.Gms);
        if (gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled"))
            return $"{was} | copied to {InstallDir} | create[{c1}] {First(o1)} | desc[{c2}] {First(o2)} | start blocked: disable GameManager first";
        var (o3, c3) = Svc.Run("sc", $"start {Svc.Own}");
        return $"{was} | copied to {InstallDir} | create[{c1}] {First(o1)} | desc[{c2}] {First(o2)} | start[{c3}] {First(o3)}";
    }

    public static string Remove()
    {
        var (o1, c1) = Svc.Run("sc", $"stop {Svc.Own}");
        var (o2, c2) = Svc.Run("sc", $"delete {Svc.Own}");
        UnregisterUninstall();
        var files = TryDeleteFiles();
        return $"stop[{c1}] {First(o1)} | delete[{c2}] {First(o2)} | {files}";
    }

    public static void ScheduleSelfCleanup()
    {
        try
        {
            var dir = InstallDir;
            var psi = new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{dir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { }
    }

    private static void CopyIfDifferent(string src, string dst)
    {
        if (string.Equals(Path.GetFullPath(src.Trim('"')), Path.GetFullPath(dst),
            StringComparison.OrdinalIgnoreCase))
            return;
        File.Copy(src, dst, overwrite: true);
    }

    private static bool IsSystemBinary(string? binPath) =>
        !string.IsNullOrEmpty(binPath) &&
        binPath.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);

    private static string DescribePrevious(string binPath, Svc.Info prev)
    {
        if (!prev.Exists) return "no previous service";
        if (string.IsNullOrEmpty(binPath)) return $"was: {prev.Status}";
        return IsSystemBinary(binPath) ? $"was system: {binPath}" : $"stopped local: {binPath}";
    }

    public static bool IsServiceFromSystemRunning()
    {
        var q = Svc.Query(Svc.Own);
        if (!string.Equals(q.Status, "Running", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsSystemBinary(GetBinaryPath());
    }

    private static string GetBinaryPath()
    {
        try
        {
            var (outp, _) = Svc.Run("sc", $"qc \"{Svc.Own}\"");
            foreach (var line in outp.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                {
                    var i = t.IndexOf(':');
                    if (i >= 0) return t[(i + 1)..].Trim().Trim('"');
                }
            }
        }
        catch { }
        return "";
    }

    private static void WaitForStop(int timeoutMs)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var q = Svc.Query(Svc.Own);
                if (!q.Exists || q.Status == "Stopped")
                    return;
                Thread.Sleep(500);
            }
        }
        catch { }
    }

    private static bool WaitForDelete(int timeoutMs)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Svc.Query(Svc.Own).Exists)
                    return true;
                Thread.Sleep(500);
            }
            return !Svc.Query(Svc.Own).Exists;
        }
        catch { return false; }
    }

    private static void RegisterUninstall()
    {
        var setup = SetupExeDst;
        var ver = "1.0.0";
        try { ver = FileVersionInfo.GetVersionInfo(SvcExeDst).FileVersion ?? ver; } catch { }
        long sizeKb = 0;
        try
        {
            sizeKb = (new FileInfo(SvcExeDst).Length + new FileInfo(setup).Length) / 1024;
        }
        catch { }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = baseKey.CreateSubKey(UninstallKeyPath);
                if (k == null) continue;
                k.SetValue("DisplayName", "rzswitch", RegistryValueKind.String);
                k.SetValue("Publisher", "rzswitch", RegistryValueKind.String);
                k.SetValue("DisplayVersion", ver, RegistryValueKind.String);
                k.SetValue("InstallLocation", InstallDir, RegistryValueKind.String);
                k.SetValue("DisplayIcon", $"\"{setup}\",0", RegistryValueKind.String);
                k.SetValue("UninstallString", $"\"{setup}\" --uninstall", RegistryValueKind.String);
                k.SetValue("QuietUninstallString", $"\"{setup}\" --uninstall", RegistryValueKind.String);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                if (sizeKb > 0) k.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    private static void UnregisterUninstall()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                baseKey.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            }
            catch { }
        }
    }

    private static string TryDeleteFiles()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return "appdata already gone";
            // running setup.exe itself is locked - delete everything else now,
            // the rest goes away via delayed rmdir after exit
            var self = Environment.ProcessPath ?? "";
            bool selfInside = !string.IsNullOrEmpty(self) &&
                self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(InstallDir))
            {
                try
                {
                    if (selfInside && string.Equals(Path.GetFullPath(f), Path.GetFullPath(self),
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Delete(f);
                }
                catch { }
            }
            if (selfInside) { ScheduleSelfCleanup(); return $"files removed, folder cleanup scheduled: {InstallDir}"; }
            try { Directory.Delete(InstallDir, recursive: true); } catch { }
            return $"removed {InstallDir}";
        }
        catch (Exception ex) { return "cleanup: " + ex.Message; }
    }

    private static string First(string s) =>
        s.Split('\n').FirstOrDefault()?.Trim() ?? "";
}

sealed class SetupForm : Form
{
    private sealed class RzButton : Button
    {
        public Color? Frame;
        public Color? Ink;
        public RzButton(string text)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 10, FontStyle.Bold);
            Size = new Size(224, 44);
            BackColor = Color.FromArgb(34, 34, 34);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        private static System.Drawing.Drawing2D.GraphicsPath Pill(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg0 = new SolidBrush(BackColor)) g.FillRectangle(bg0, ClientRectangle);
            var on = Enabled && !DesignMode;
            var isHover = on && ClientRectangle.Contains(PointToClient(Cursor.Position));
            Color bg, fg;
            if (!on) { bg = Color.FromArgb(17, 17, 17); fg = Color.FromArgb(85, 85, 85); }
            else { bg = isHover ? Color.FromArgb(42, 42, 42) : Color.FromArgb(17, 17, 17); fg = isHover ? Color.White : Color.FromArgb(204, 204, 204); }
            // Inset by 1px: Region выкинут (он 1-битный и дает рваный край),
            // пилюля рисуется внутри прямоугольного контрола — край
            // сглаживается о фон BackColor, перо 2px не клиппится.
            var rect = ClientRectangle;
            rect.Inflate(-1, -1);
            using var path = Pill(rect, rect.Height / 2);
            if (Frame.HasValue)
            {
                var ink = Ink ?? Frame.Value;
                if (isHover)
                {
                    using var hb = new SolidBrush(Color.FromArgb(28, Frame.Value.R, Frame.Value.G, Frame.Value.B));
                    g.FillPath(hb, path);
                }
                using var pen = new Pen(Frame.Value, 2);
                pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Center;
                g.DrawPath(pen, path);
                using var brush2 = new SolidBrush(on ? ink : Color.FromArgb(120, 110, 100));
                using var fmt2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Text, Font, brush2, rect, fmt2);
                return;
            }
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
            using var brush = new SolidBrush(fg);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, brush, rect, fmt);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    private readonly RzButton _bGmsDisable = new("Disable GameManager");
    private readonly RzButton _bGmsEnable = new("Enable GameManager");
    private readonly RzButton _bOwnSetup = new("Install & Start our service");
    private readonly RzButton _bOwnRemove = new("Remove our service");
    private readonly RzButton _bSysInstall = new("Install to system");
    private readonly RzButton _bSysRemove = new("Remove from system");
    private readonly Label _gmsLabel = new();
    private readonly Label _svcLabel = new();
    private readonly Label _sysLabel = new();
    private readonly bool _uninstallMode;
    private readonly Label _hint = new();
    private readonly RichTextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private Point _drag;

    public SetupForm(bool uninstallMode = false)
    {
        _uninstallMode = uninstallMode;
        Text = "rzswitch // setup";
        BackColor = Color.FromArgb(34, 34, 34);
        ForeColor = Color.FromArgb(204, 204, 204);
        Size = new Size(520, 536);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        using (var rp = new System.Drawing.Drawing2D.GraphicsPath())
        {
            int rad = 16, d = rad * 2;
            var r = new Rectangle(0, 0, Width, Height);
            rp.AddArc(r.X, r.Y, d, d, 180, 90);
            rp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            rp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            rp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            rp.CloseFigure();
            Region = new Region(rp);
        }

        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(520, 70),
            BackColor = Color.FromArgb(34, 34, 34),
        };
        Controls.Add(header);
        var title = new Label
        {
            Text = "RZSWITCH // SETUP",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            Location = new Point(16, 8),
            AutoSize = true,
        };
        header.Controls.Add(title);
        var sub = new Label
        {
            Text = "game profile auto-switch | installer",
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 9),
            Location = new Point(18, 40),
            AutoSize = true,
        };
        header.Controls.Add(sub);
        var coffee = new RzButton("Buy me a coffee")
        {
            Frame = Color.FromArgb(255, 150, 50),
            Ink = Color.FromArgb(255, 170, 80),
            Size = new Size(150, 28),
            Location = new Point(316, 21),
            Cursor = Cursors.Hand,
        };
        coffee.Font = new Font("Segoe UI", 9);
        coffee.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://destream.net/live/CucumberKing/donate") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "rzswitch", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        header.Controls.Add(coffee);
        var close = new Label
        {
            Text = "X",
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Location = new Point(482, 19),
            Size = new Size(30, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        close.MouseEnter += (_, _) => close.ForeColor = Color.Red;
        close.MouseLeave += (_, _) => close.ForeColor = Color.FromArgb(120, 120, 120);
        close.Click += (_, _) => Close();
        header.Controls.Add(close);
        header.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _drag = e.Location; } };
        header.MouseMove += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { Left += e.X - _drag.X; Top += e.Y - _drag.Y; }
        };

        _gmsLabel.Text = "G A M E   M A N A G E R";
        _gmsLabel.ForeColor = Color.FromArgb(150, 150, 150);
        _gmsLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _gmsLabel.Location = new Point(20, 74);
        _gmsLabel.AutoSize = true;
        Controls.Add(_gmsLabel);

        _svcLabel.Text = "R Z S W I T C H — RUNS FROM CURRENT FOLDER";
        _svcLabel.ForeColor = Color.FromArgb(150, 150, 150);
        _svcLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _svcLabel.Location = new Point(20, 148);
        _svcLabel.AutoSize = true;
        Controls.Add(_svcLabel);

        _sysLabel.Text = "S Y S T E M — COPIES FILES TO APPDATA";
        _sysLabel.ForeColor = Color.FromArgb(150, 150, 150);
        _sysLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _sysLabel.Location = new Point(20, 222);
        _sysLabel.AutoSize = true;
        Controls.Add(_sysLabel);

        _hint.ForeColor = Color.FromArgb(140, 140, 140);
        _hint.Font = new Font("Segoe UI", 9, FontStyle.Italic);
        _hint.Location = new Point(20, 296);
        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(484, 0);
        _hint.UseMnemonic = false;
        Controls.Add(_hint);

        var buttons = new[] { _bGmsDisable, _bGmsEnable, _bOwnSetup, _bOwnRemove, _bSysInstall, _bSysRemove };
        int[] rows = { 96, 170, 244 };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Location = new Point(18 + (i % 2) * 236, rows[i / 2]);
            Controls.Add(buttons[i]);
        }
        int y = 318;

        _bGmsDisable.Click += (_, _) => Do("GameManager disabled", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"stop \"{Svc.Gms}\"");
            var (o2, c2) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= disabled");
            return $"stop[{c1}] {FirstLine(o1)} | config[{c2}] {FirstLine(o2)}";
        });
        _bGmsEnable.Click += (_, _) => Do("GameManager enabled", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= demand");
            var (o2, c2) = Svc.Run("sc", $"start \"{Svc.Gms}\"");
            return $"config[{c1}] {FirstLine(o1)} | start[{c2}] {FirstLine(o2)}";
        });
        _bOwnSetup.Click += (_, _) =>
        {
            if (!Svc.Query(Svc.Own).Exists)
            {
                Do("service installed", () =>
                {
                    var exe = Svc.ServiceExe();
                    if (!File.Exists(exe)) return $"missing {exe} - keep setup next to rzswitch.exe";
                    var (o1, c1) = Svc.Run("sc", $"create {Svc.Own} binPath= \"{exe}\" start= auto");
                    var (o2, c2) = Svc.Run("sc", $"description {Svc.Own} \"Razer GameManager replacement: foreground detector + SignalR hub\"");
                    return $"create[{c1}] {FirstLine(o1)} | desc[{c2}] {FirstLine(o2)}";
                });
            }
            var gms = Svc.Query(Svc.Gms);
            if (gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled"))
            {
                Log("Installed, but start blocked: GameManager is active - disable it first.");
                RefreshStatus();
                return;
            }
            Do("our service started", () =>
            {
                var (o, c) = Svc.Run("sc", $"start {Svc.Own}");
                return $"start[{c}] {FirstLine(o)}";
            });
        };
        _bOwnRemove.Click += (_, _) => Do("service removed", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"stop {Svc.Own}");
            var (o2, c2) = Svc.Run("sc", $"delete {Svc.Own}");
            return $"stop[{c1}] {FirstLine(o1)} | delete[{c2}] {FirstLine(o2)}";
        });
        _bSysInstall.Click += (_, _) => Do("installed to system", SysInstall.Install);
        _bSysRemove.Click += (_, _) =>
        {
            Do("removed from system", SysInstall.Remove);
            if (_uninstallMode)
            {
                MessageBox.Show("Удаление завершено", "rzswitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
        };

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = RichTextBoxScrollBars.None;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(17, 17, 17);
        _log.ForeColor = Color.FromArgb(204, 204, 204);
        _log.Font = new Font("Consolas", 9);
        _log.Location = new Point(18, y);
        _log.Size = new Size(484, 190);
        Controls.Add(_log);
        _log.BringToFront();

        _timer.Interval = 2000;
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        if (_uninstallMode)
        {
            Text = "rzswitch // uninstall";
            _sysLabel.Text = "S Y S T E M — REMOVES SYSTEM COPY COMPLETELY";
            // hide install-without-copy rows, keep only system-level removal
            _gmsLabel.Visible = false;
            _svcLabel.Visible = false;
            _bGmsDisable.Visible = false;
            _bGmsEnable.Visible = false;
            _bOwnSetup.Visible = false;
            _bOwnRemove.Visible = false;
            _bSysInstall.Visible = false;
            _sysLabel.Location = new Point(20, 74);
            _bSysRemove.Location = new Point(18, 96);
            _hint.Location = new Point(20, 148);
            _log.Location = new Point(18, 170);
            _log.Size = new Size(484, 338);
            _timer.Stop();
            Log($"System install dir: {SysInstall.InstallDir}");
            Log("Press \"Remove from system\" to uninstall.");
            RefreshStatus();
            return;
        }
        RefreshStatus();
        var svc = Svc.Query(Svc.Own);
        if (svc.Exists && svc.Status == "Running")
            Log("Service is installed and running.", Color.FromArgb(68, 221, 34));
    }

    private static string FirstLine(string s) =>
        s.Split('\n').FirstOrDefault()?.Trim() ?? "";

    private void Log(string s, Color? color = null)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color ?? _log.ForeColor;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private void Do(string label, Func<string> act)
    {
        string outp;
        try { outp = act(); }
        catch (Exception ex) { outp = "ERR: " + ex.Message; }
        Log(label + ": " + outp.Replace("\n", " | "));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var gms = Svc.Query(Svc.Gms);
        var own = Svc.Query(Svc.Own);
        bool gmsActive = gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled");
        bool sysInstalled = SysInstall.IsInstalled();
        if (_uninstallMode)
        {
            _hint.Text = sysInstalled
                ? "rzswitch is installed in the system - press Remove."
                : "Nothing to remove.";
            _bSysRemove.Enabled = sysInstalled;
            return;
        }
        _hint.Text = gmsActive
            ? "GameManager is active - disable it to unlock our service."
            : !own.Exists ? "Install the service to begin."
            : own.Status != "Running" ? "Service is here - press Install & Start."
            : "Running. Profile auto-switch is on.";
        _hint.Text += sysInstalled
            ? $" System copy: {SysInstall.InstallDir}."
            : " No system copy.";
        _bOwnSetup.Enabled = own.Status != "Running";
        _bOwnRemove.Enabled = own.Exists;
        _bGmsDisable.Enabled = gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled");
        _bGmsEnable.Enabled = gms.Exists && (gms.Status != "Running" || gms.StartType == "disabled") && own.Status != "Running";
        _bSysRemove.Enabled = sysInstalled;
        _bSysInstall.Enabled = !SysInstall.IsServiceFromSystemRunning();
    }
}

static class Entry
{
    [STAThread]
    static void Main(string[] args)
    {
        bool uninstall = args.Any(a =>
            a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
            || a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase)
            || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
            || a.Equals("uninstall", StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(uninstall));
    }
}
