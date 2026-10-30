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
using System.Windows.Forms;

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

sealed class SetupForm : Form
{
    private sealed class RzButton : Button
    {
        public RzButton(string text)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 10, FontStyle.Bold);
            Size = new Size(224, 44);
            BackColor = Color.FromArgb(34, 34, 34);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
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
            using (var bg0 = new SolidBrush(BackColor)) g.FillRectangle(bg0, ClientRectangle);
            var on = Enabled && !DesignMode;
            var isHover = on && ClientRectangle.Contains(PointToClient(Cursor.Position));
            Color bg, fg;
            if (!on) { bg = Color.FromArgb(17, 17, 17); fg = Color.FromArgb(85, 85, 85); }
            else { bg = isHover ? Color.FromArgb(42, 42, 42) : Color.FromArgb(17, 17, 17); fg = isHover ? Color.White : Color.FromArgb(204, 204, 204); }
            using var path = Pill(ClientRectangle, Height / 2);
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
            using var brush = new SolidBrush(fg);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, brush, ClientRectangle, fmt);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    private readonly RzButton _bGmsDisable = new("Disable GameManager");
    private readonly RzButton _bGmsEnable = new("Enable GameManager");
    private readonly RzButton _bOwnSetup = new("Install & Start our service");
    private readonly RzButton _bOwnRemove = new("Remove our service");
    private readonly Label _hint = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private Point _drag;

    public SetupForm()
    {
        Text = "rzswitch // setup";
        BackColor = Color.FromArgb(34, 34, 34);
        ForeColor = Color.FromArgb(204, 204, 204);
        Size = new Size(520, 462);
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
        var close = new Label
        {
            Text = "X",
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Location = new Point(482, 10),
            AutoSize = true,
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

        var gmsLabel = new Label
        {
            Text = "G A M E   M A N A G E R",
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(20, 74),
            AutoSize = true,
        };
        Controls.Add(gmsLabel);

        var svcLabel = new Label
        {
            Text = "R Z S W I T C H   S E R V I C E",
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(20, 148),
            AutoSize = true,
        };
        Controls.Add(svcLabel);

        _hint.ForeColor = Color.FromArgb(140, 140, 140);
        _hint.Font = new Font("Segoe UI", 9, FontStyle.Italic);
        _hint.Location = new Point(20, 222);
        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(484, 0);
        _hint.UseMnemonic = false;
        Controls.Add(_hint);

        var buttons = new[] { _bGmsDisable, _bGmsEnable, _bOwnSetup, _bOwnRemove };
        int[] rows = { 96, 170 };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Location = new Point(18 + (i % 2) * 236, rows[i / 2]);
            Controls.Add(buttons[i]);
        }
        int y = 244;

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

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.None;
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
        RefreshStatus();
    }

    private static string FirstLine(string s) =>
        s.Split('\n').FirstOrDefault()?.Trim() ?? "";

    private void Log(string s)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
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
        _hint.Text = gmsActive
            ? "GameManager is active - disable it to unlock our service."
            : !own.Exists ? "Install the service to begin."
            : own.Status != "Running" ? "Service is here - press Install & Start."
            : "Running. Profile auto-switch is on.";
        _bOwnSetup.Enabled = own.Status != "Running";
        _bOwnRemove.Enabled = own.Exists;
        _bGmsDisable.Enabled = gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled");
        _bGmsEnable.Enabled = gms.Exists && (gms.Status != "Running" || gms.StartType == "disabled");
    }
}

static class Entry
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}
