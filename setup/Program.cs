// rzswitch-setup: installer panel (WinForms, Razer-styled).
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
                return new Info(false, "—", "—");
            using var sc = new ServiceController(name);
            string status;
            try { status = sc.Status.ToString(); }
            catch { status = "unknown"; }
            return new Info(true, status, StartMode(name));
        }
        catch { return new Info(false, "—", "—"); }
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
        public RzButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 10, FontStyle.Bold);
            Size = new Size(224, 40);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var on = Enabled;
            var fg = on ? Color.Lime : Color.FromArgb(0, 110, 0);
            var border = on ? Color.Lime : Color.FromArgb(0, 80, 0);
            g.Clear(Color.Black);
            using var pen = new Pen(border, 2);
            g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            using var brush = new SolidBrush(fg);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, brush, ClientRectangle, fmt);
            if (on && (Focused || ContainsFocus))
            {
                using var glow = new Pen(Color.FromArgb(90, 0, 255, 0), 4);
                g.DrawRectangle(glow, 3, 3, Width - 7, Height - 7);
            }
        }
    }

    private readonly Label _gmsState = new();
    private readonly Label _ownState = new();
    private readonly RzButton _bGmsDisable = new();
    private readonly RzButton _bGmsEnable = new();
    private readonly RzButton _bOwnInstall = new();
    private readonly RzButton _bOwnRemove = new();
    private readonly RzButton _bOwnStart = new();
    private readonly RzButton _bOwnStop = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    public SetupForm()
    {
        Text = "rzswitch // setup";
        BackColor = Color.Black;
        ForeColor = Color.FromArgb(201, 212, 201);
        Size = new Size(520, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "RZSWITCH // SETUP",
            ForeColor = Color.Lime,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true,
        };
        Controls.Add(title);
        var sub = new Label
        {
            Text = "installer panel — запуск от администратора",
            ForeColor = Color.DimGray,
            Location = new Point(18, 44),
            AutoSize = true,
        };
        Controls.Add(sub);

        int y = 76;
        _gmsState.Location = new Point(18, y); _gmsState.AutoSize = true; _gmsState.Font = new Font("Consolas", 10);
        Controls.Add(_gmsState); y += 26;
        _ownState.Location = new Point(18, y); _ownState.AutoSize = true; _ownState.Font = new Font("Consolas", 10);
        Controls.Add(_ownState); y += 34;

        _bGmsDisable.Text = "Выключить GameManager";
        _bGmsEnable.Text = "Включить GameManager";
        _bOwnInstall.Text = "Установить наш сервис";
        _bOwnRemove.Text = "Удалить наш сервис";
        _bOwnStart.Text = "Включить наш сервис";
        _bOwnStop.Text = "Выключить наш сервис";
        var buttons = new[] { _bGmsDisable, _bGmsEnable, _bOwnInstall, _bOwnRemove, _bOwnStart, _bOwnStop };
        foreach (var b in buttons)
        {
            b.Size = new Size(224, 40);
            b.Location = new Point(18 + (Array.IndexOf(buttons, b) % 2) * 236, y + (Array.IndexOf(buttons, b) / 2) * 48);
            Controls.Add(b);
        }
        y += 3 * 48 + 12;
        _bGmsDisable.Click += (_, _) => Do("GMS выключен", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"stop \"{Svc.Gms}\"");
            var (o2, c2) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= disabled");
            return $"stop[{c1}] {FirstLine(o1)}\nconfig[{c2}] {FirstLine(o2)}";
        });
        _bGmsEnable.Click += (_, _) => Do("GMS включен", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= demand");
            var (o2, c2) = Svc.Run("sc", $"start \"{Svc.Gms}\"");
            return $"config[{c1}] {FirstLine(o1)}\nstart[{c2}] {FirstLine(o2)}";
        });
        _bOwnInstall.Click += (_, _) => Do("сервис установлен", () =>
        {
            var exe = Svc.ServiceExe();
            if (!File.Exists(exe)) return $"не найден {exe} — положите setup рядом с rzswitch.exe";
            var (o1, c1) = Svc.Run("sc", $"create {Svc.Own} binPath= \"{exe}\" start= auto");
            var (o2, c2) = Svc.Run("sc", $"description {Svc.Own} \"Razer GameManager replacement: foreground detector + SignalR hub\"");
            return $"create[{c1}] {FirstLine(o1)}\ndesc[{c2}] {FirstLine(o2)}";
        });
        _bOwnRemove.Click += (_, _) => Do("сервис удален", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"stop {Svc.Own}");
            var (o2, c2) = Svc.Run("sc", $"delete {Svc.Own}");
            return $"stop[{c1}] {FirstLine(o1)}\ndelete[{c2}] {FirstLine(o2)}";
        });
        _bOwnStart.Click += (_, _) =>
        {
            var gms = Svc.Query(Svc.Gms);
            if (gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled"))
            {
                Log("Отказ: GameManager активен — сначала выключите его.");
                return;
            }
            Do("наш сервис запущен", () =>
            {
                var (o, c) = Svc.Run("sc", $"start {Svc.Own}");
                return $"start[{c}] {FirstLine(o)}";
            });
        };
        _bOwnStop.Click += (_, _) => Do("наш сервис остановлен", () =>
        {
            var (o, c) = Svc.Run("sc", $"stop {Svc.Own}");
            return $"stop[{c}] {FirstLine(o)}";
        });

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.Black;
        _log.ForeColor = Color.Lime;
        _log.Font = new Font("Consolas", 9);
        _log.Location = new Point(18, y);
        _log.Size = new Size(460, 180);
        Controls.Add(_log);

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
        _gmsState.Text = $"GMS:   {(gms.Status == "Running" ? "●" : "○")} {gms.Status} / {gms.StartType}";
        _gmsState.ForeColor = gms.Status == "Running" ? Color.Lime : Color.Gray;
        _ownState.Text = $"OURS:  {(own.Status == "Running" ? "●" : "○")} {own.Status} / {own.StartType}";
        _ownState.ForeColor = own.Status == "Running" ? Color.Lime : Color.Gray;
        bool gmsActive = gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled");
        _bOwnStart.Enabled = !gmsActive;
        _bOwnInstall.Enabled = own is { Exists: false };
        _bOwnRemove.Enabled = own.Exists;
        _bOwnStop.Enabled = own.Status == "Running";
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
