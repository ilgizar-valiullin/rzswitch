// rzswitch: GameManagerService3 replacement + control panel.
// Hosts SignalR hub at /GameManagerServiceHub (same contract as Razer),
// watches the foreground window and pushes GameStarted/Stopped so that
// Synapse applies linked profiles. Web UI on / for service management.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;
using Microsoft.AspNetCore.Authorization;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ================= DTOs (wire format = Razer GameScannerCommon) =================
public enum DetectMethod
{
    NONE, PREVIOUS_RESULT, ARC_REGISTRY, BETHESDA_REGISTRY, EA_REGISTRY,
    GOG_REGISTRY, ITCHIO_DB, TWITCH_DB, UPLAY_DB, EPIC_LAUNCHER, REGISTRY,
    REGISTRY_KEY, GAMEUX_INFO, GAMEUX_REGISTRY, REGISTRY_UNINSTALL,
    REGISTRY_DEFAULT, EXE_PATH, USER_FOLDER_EXE_PATH, EXE_PATH_AUTODETECT,
    MANUAL, STEAM_ID, STEAM_ID_DEEP_SEARCH, STEAM_NOT_IN_C, STEAM_DYNAMIC,
    STEAM_LIBRARY, STEAM_SHORTCUTS, BATTLENET_AGENT_DB, BATTLENET_AGENT_DB_V2,
    RIOT_PLATFORM, MIHOYO_PLATFORM, FEVER_PLATFORM
}

public enum GamePlatform { WINDOWS, OSX, LINUX, OTHER }

public sealed class GameSignature
{
    [JsonPropertyName("entry_guid")] public Guid EntryGuid { get; set; }
    [JsonPropertyName("display_name32_bit")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("game_title")] public string GameTitle { get; set; } = "";
    [JsonPropertyName("launch_exe")] public string LaunchExe { get; set; } = "";
    [JsonPropertyName("process_name")] public string ProcessName { get; set; } = "";
    [JsonPropertyName("default_exe")] public string DefaultExe { get; set; } = "";
    [JsonPropertyName("default_exe32_bit")] public string DefaultExe32 { get; set; } = "";
    [JsonPropertyName("default_exe64_bit")] public string DefaultExe64 { get; set; } = "";
    [JsonPropertyName("detecting_method")]
    [JsonConverter(typeof(JsonStringEnumConverter<DetectMethod>))]
    public DetectMethod DetectingMethod { get; set; } = DetectMethod.MANUAL;
    [JsonPropertyName("platform")]
    [JsonConverter(typeof(JsonStringEnumConverter<GamePlatform>))]
    public GamePlatform PlatForm { get; set; } = GamePlatform.WINDOWS;
    [JsonPropertyName("source_ex")] public string SourceEx { get; set; } = "DIRECT";
    [JsonPropertyName("machine_id")] public string MachineId { get; set; } = "";
}

public sealed class GameSignatureWithPid
{
    [JsonPropertyName("pid")] public uint Pid { get; set; }
    [JsonPropertyName("signature")] public GameSignature Signature { get; set; } = new();
}

public sealed class GameSignatureWithDuration
{
    [JsonPropertyName("duration")] public TimeSpan Duration { get; set; }
    [JsonPropertyName("signature")] public GameSignature Signature { get; set; } = new();
}

// Client-callback payloads. Razer wraps every signature in a named property
// (GameSignatureEventArgs.GameSignature etc.), so a bare GameSignature arrives
// on the client as e.gameSignature === undefined and the handler throws.
public sealed class GameSignatureEventArgs
{
    [JsonPropertyName("gameSignature")] public GameSignature GameSignature { get; set; } = new();
}

public sealed class GameSignatureWithPidEventArgs
{
    [JsonPropertyName("gameSignatureWithPid")] public GameSignatureWithPid GameSignatureWithPid { get; set; } = new();
}

public sealed class GameSignatureWithDurationEventArgs
{
    [JsonPropertyName("gameSignatureWithDuration")] public GameSignatureWithDuration GameSignatureWithDuration { get; set; } = new();
}

public sealed class PartialGameSignatureList
{
    [JsonPropertyName("gameListInfo")] public object? GameListInfo { get; set; }
    [JsonPropertyName("gameSignatureList")] public List<GameSignature> SignatureList { get; set; } = new();
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("totalSize")] public int TotalSize { get; set; }
}

public sealed class GameSettingsList
{
    [JsonPropertyName("settingsList")] public List<object> SettingsList { get; set; } = new();
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<GameScannerErrorCode>))]
public enum GameScannerErrorCode
{
    GS_SUCCESS,
    GS_FAILED,
    GS_SCAN_IN_PROGRESS,
    GS_NOT_CONFIGURED,
}

public sealed class ForegroundUpdate
{
    [JsonPropertyName("pid")] public uint Pid { get; set; }
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
}

[JsonSerializable(typeof(ForegroundUpdate))]
[JsonSerializable(typeof(GameSignatureEventArgs))]
[JsonSerializable(typeof(GameSignatureWithPidEventArgs))]
[JsonSerializable(typeof(GameSignatureWithDurationEventArgs))]
[JsonSerializable(typeof(GameSignature))]
[JsonSerializable(typeof(GameSignatureWithPid))]
[JsonSerializable(typeof(GameSignatureWithDuration))]
[JsonSerializable(typeof(PartialGameSignatureList))]
[JsonSerializable(typeof(GameSettingsList))]
[JsonSerializable(typeof(GameScannerErrorCode))]
public partial class RzJsonContext : JsonSerializerContext { }

// ================= hub =================
// Mirrors Razer.GameScannerCommon.IGameManagerServiceHubClient.
public interface IRzPush
{
    Task OnGameStartedEvent(GameSignatureEventArgs args);
    Task OnGameStartedWithPidEvent(GameSignatureWithPidEventArgs args);
    Task OnGameStoppedEvent(GameSignatureEventArgs args);
    Task OnGameStoppedWithPidEvent(GameSignatureWithPidEventArgs args);
    Task OnGameStoppedWithDurationEvent(GameSignatureWithDurationEventArgs args);
}

[AllowAnonymous]
public sealed class RzHub : Hub<IRzPush>
{
    private const int MaxGameListSize = 16;
    private readonly ILogger<RzHub> _log;
    private readonly RzState _state;
    public RzHub(ILogger<RzHub> log, RzState state) { _log = log; _state = state; }
    public override Task OnConnectedAsync()
    {
        _log.LogInformation("hub client connected: {id}", Context.ConnectionId);
        if (_state.CurrentExe != null && _state.Games.TryGetValue(_state.CurrentExe, out var sig))
        {
            var args = new GameSignatureEventArgs { GameSignature = sig };
            _ = Clients.Client(Context.ConnectionId).OnGameStartedEvent(args);
            _log.LogInformation("OnConnectedAsync: sent OnGameStartedEvent exe={exe} guid={guid} payload={payload}",
                _state.CurrentExe, sig.EntryGuid, JsonSerializer.Serialize(args));
        }
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _log.LogInformation("hub client disconnected: {id} err={err}", Context.ConnectionId, exception?.Message);
        return base.OnDisconnectedAsync(exception);
    }
    public string Ping(string? msg)
    {
        _log.LogInformation("Ping({m})", msg);
        return "pong";
    }

    // ================= Synapse client RPCs =================
    public GameScannerErrorCode ConfigSetUniqueUserIdWithClientVersion(string appName, string userId, int clientVersion)
    {
        _state.UserId = userId ?? "";
        _log.LogInformation("ConfigSetUniqueUserIdWithClientVersion app={app} user={user} clientVer={cv}", appName, userId, clientVersion);
        return GameScannerErrorCode.GS_SUCCESS;
    }

    public string GetServiceVersion()
    {
        var v = _state.ServiceVersion;
        _log.LogInformation("GetServiceVersion -> {v}", v);
        return v;
    }

    public bool IsScanning() => false;

    public GameScannerErrorCode StartScan()
    {
        _log.LogInformation("StartScan");
        return GameScannerErrorCode.GS_SUCCESS;
    }

    public GameScannerErrorCode StopScan() => GameScannerErrorCode.GS_SUCCESS;

    public async IAsyncEnumerable<PartialGameSignatureList> GetLocalGameListPartial(int offset)
    {
        _log.LogInformation("GetLocalGameListPartial offset={offset}", offset);
        await foreach (var page in Pages(_state.Games.Values.ToList(), offset))
            yield return page;
    }

    public async IAsyncEnumerable<PartialGameSignatureList> GetUserGameListPartial(int offset)
    {
        _log.LogInformation("GetUserGameListPartial offset={offset}", offset);
        await foreach (var page in Pages(_state.Games.Values.ToList(), offset))
            yield return page;
    }

    public GameSettingsList GetGameSettingsList()
    {
        _log.LogInformation("GetGameSettingsList user={u}", _state.UserId);
        return new GameSettingsList { UserId = _state.UserId };
    }

    public Task<List<GameSignatureWithPid>> GetCurrentRunningGamesListAsync()
    {
        _log.LogInformation("GetCurrentRunningGamesListAsync");
        var list = new List<GameSignatureWithPid>();
        if (_state.CurrentExe != null && _state.Games.TryGetValue(_state.CurrentExe, out var sig))
        {
            list.Add(new GameSignatureWithPid { Pid = _state.CurrentPid, Signature = sig });
        }
        return Task.FromResult(list);
    }

    // Razer streams the game list in pages of 16 and always yields at least one page.
    private async IAsyncEnumerable<PartialGameSignatureList> Pages(List<GameSignature> all, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > all.Count) offset = all.Count;
        var total = all.Count;
        do
        {
            var take = Math.Min(MaxGameListSize, total - offset);
            var page = new PartialGameSignatureList
            {
                UserId = _state.UserId,
                Offset = offset,
                TotalSize = total,
                SignatureList = all.GetRange(offset, take),
            };
            offset += take;
            yield return page;
            await Task.Yield();
        }
        while (total > offset);
    }
}

public sealed class RzState
{
    public Dictionary<string, GameSignature> Games = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentExe;
    public uint CurrentPid;
    public DateTime CurrentStartedAt;
    public string UserId = "";
    public string ServiceVersion = "3.15.0.1200";
    public string LastEvent = "—";
    public int BoundPort;
}

// ================= foreground watcher =================
internal static class Foreground
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, [Out] char[] name, ref uint size);

    public static (uint pid, string exe) Get()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return (0, "");
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return (0, "");
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return (pid, "");
        try
        {
            var buf = new char[1024]; uint n = 1024;
            if (!QueryFullProcessImageName(h, 0, buf, ref n)) return (pid, "");
            var full = new string(buf, 0, (int)n);
            var i = full.LastIndexOf('\\');
            return (pid, i >= 0 ? full[(i + 1)..] : full);
        }
        finally { CloseHandle(h); }
    }
}

internal static class GameList
{
    public static Dictionary<string, GameSignature> Load(ILogger log)
    {
        var map = new Dictionary<string, GameSignature>(StringComparer.OrdinalIgnoreCase);
        const string data = @"C:\ProgramData\Razer\GameManager3\Data";
        if (!Directory.Exists(data)) return map;
        foreach (var dir in Directory.GetDirectories(data, "rzr_*"))
        {
            foreach (var f in Directory.GetFiles(dir, "UserGameList_*.xml"))
            {
                try
                {
                    var doc = new System.Xml.XmlDocument();
                    doc.Load(f);
                    foreach (System.Xml.XmlNode n in doc.GetElementsByTagName("GameSignature"))
                    {
                        string Sel(string x) => n.SelectSingleNode(x)?.InnerText ?? "";
                        static string BaseName(string v)
                        {
                            var s = Math.Max(v.LastIndexOf('\\'), v.LastIndexOf('/'));
                            return s >= 0 ? v[(s + 1)..] : v;
                        }
                        var exe = Sel("DefaultExe");
                        if (string.IsNullOrWhiteSpace(exe)) exe = Sel("LaunchExe");
                        if (string.IsNullOrWhiteSpace(exe)) continue;
                        exe = BaseName(exe.Trim());
                        if (Guid.TryParse(Sel("EntryGUID"), out var g) && g != Guid.Empty)
                        {
                            map[exe] = new GameSignature
                            {
                                EntryGuid = g,
                                DisplayName = Sel("DisplayName"),
                                GameTitle = Sel("GameTitle"),
                                LaunchExe = Sel("LaunchExe"),
                                ProcessName = Sel("ProcessName"),
                                DefaultExe = exe,
                                DefaultExe32 = BaseName(Sel("DefaultExe32").Trim()),
                                DefaultExe64 = BaseName(Sel("DefaultExe64").Trim()),
                            };
                        }
                    }
                    log.LogInformation("loaded {n} games from {f}", map.Count, f);
                }
                catch (Exception ex) { log.LogWarning("game list parse failed: {m}", ex.Message); }
            }
        }
        return map;
    }
}

// ================= services control (sc.exe) =================
internal static class Svc
{
    public const string Gms = "Razer Game Manager Service 3";
    public const string Own = "rzswitch";

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public record Info(bool Exists, string Status, string StartType);

    public static Info Query(string name)
    {
        try
        {
            var exists = ServiceController.GetServices()
                .Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));
            if (!exists) return new Info(false, "—", "—");
            using var sc = new ServiceController(name);
            string status;
            try { status = sc.Status.ToString(); } catch { status = "unknown"; }
            return new Info(true, status, StartMode(name));
        }
        catch { return new Info(false, "—", "—"); }
    }

    private static string StartMode(string name)
    {
        try
        {
            var (code, _) = Run("sc", $"qc \"{name}\"");
            foreach (var line in code.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("START_TYPE"))
                {
                    // e.g. "START_TYPE         : 2   AUTO_START"
                    var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var last = parts[^1];
                    return last switch
                    {
                        "AUTO_START" => "auto",
                        "DEMAND_START" => "demand",
                        "DISABLED" => "disabled",
                        _ => last,
                    };
                }
            }
        }
        catch { }
        return "?";
    }

    public static (string outp, int code) Run(string exe, string args)
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
            p.WaitForExit(15000);
            return (o.Trim(), p.ExitCode);
        }
        catch (Exception ex) { return (ex.Message, -1); }
    }

    public static string ExePath() =>
        Path.Combine(AppContext.BaseDirectory, "rzswitch.exe");
}

// ================= GMS guard: fastest status check, no enumeration, no child processes =================
internal static class GmsGuard
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SC_STATUS_PROCESS_INFO = 0;

    private const int SERVICE_STOPPED = 0x1;
    private const int SERVICE_START_PENDING = 0x2;
    private const int SERVICE_STOP_PENDING = 0x3;
    private const int SERVICE_RUNNING = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
        public int dwProcessId;
        public int dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr hSC, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr h, int level, ref SERVICE_STATUS_PROCESS buf, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);

    // null = no such service (or SCM unavailable); otherwise SCM state code
    public static int? State(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return null;
            try
            {
                var st = new SERVICE_STATUS_PROCESS();
                if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref st, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                    return null;
                return st.dwCurrentState;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool IsActive(string name, out int state)
    {
        var s = State(name);
        state = s ?? -1;
        return s is SERVICE_RUNNING or SERVICE_START_PENDING;
    }
}

// ================= UI =================
internal static class Ui
{
    public const string Green = "#00ff00";
    public static string Page(RzState s, Svc.Info gms, Svc.Info own, bool admin, string msg)
    {
        string led(string on) => on == "Running"
            ? "<span class='led on'></span>"
            : "<span class='led off'></span>";
        string gmsBlocked = (gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled")) ? "disabled" : "";
        string gmsWhy = gmsBlocked == "" ? "" : "<div class='warn'>GameManager активен — сначала выключите его (конфликт порта/реестра)</div>";
        string adminWarn = admin ? "" : "<div class='warn'>Панель запущена БЕЗ прав администратора: кнопки sc.exe не сработают. Перезапустите от администратора.</div>";
        string msgDiv = string.IsNullOrEmpty(msg) ? "" : $"<div class='msg'>{System.Net.WebUtility.HtmlEncode(msg)}</div>";
        string esc(string x) => System.Net.WebUtility.HtmlEncode(x);
        return $@"<!DOCTYPE html><html lang='ru'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>rzswitch // control</title>
<style>
:root{{--rz:{Green};--bg:#050505;--panel:#0b0e0b;--line:#123312;--txt:#c9d4c9;--dim:#5a6b5a}}
*{{box-sizing:border-box}}body{{background:var(--bg);color:var(--txt);
font-family:'Segoe UI',Roboto,Arial,sans-serif;margin:0;padding:24px}}
h1{{color:var(--rz);letter-spacing:4px;font-size:22px;margin:0 0 4px;
text-shadow:0 0 12px var(--rz)}}.sub{{color:var(--dim);font-size:12px;margin-bottom:20px}}
.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:16px;max-width:1100px}}
.card{{background:var(--panel);border:1px solid var(--line);border-radius:6px;
padding:16px;box-shadow:0 0 18px rgba(0,255,0,.06) inset}}
.card h2{{margin:0 0 12px;font-size:13px;letter-spacing:2px;color:var(--rz)}}
.row{{display:flex;justify-content:space-between;padding:5px 0;font-size:14px;
border-bottom:1px dashed #152015}}.row b{{font-family:Consolas,monospace}}
.mono{{font-family:Consolas,monospace}}.led{{display:inline-block;width:10px;height:10px;
border-radius:50%;margin-right:8px}}.led.on{{background:var(--rz);box-shadow:0 0 8px var(--rz)}}
.led.off{{background:#3a1414;box-shadow:0 0 8px #a00}}
button{{background:#000;color:var(--rz);border:1px solid var(--rz);border-radius:4px;
padding:9px 14px;margin:4px 4px 0 0;cursor:pointer;font-weight:700;letter-spacing:1px}}
button:hover:not(:disabled){{background:var(--rz);color:#000;box-shadow:0 0 14px var(--rz)}}
button:disabled{{opacity:.25;cursor:not-allowed}}
.warn{{border:1px solid #a00;color:#f66;padding:10px;border-radius:4px;margin:12px 0;max-width:1100px}}
.msg{{border:1px solid var(--rz);padding:10px;border-radius:4px;margin:12px 0;max-width:1100px;white-space:pre-wrap}}
.log{{background:#000;border:1px solid var(--line);border-radius:6px;padding:12px;
font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap;max-height:260px;
overflow-y:auto;max-width:1100px;margin-top:16px}}
a{{color:var(--rz)}}
</style></head><body>
<h1>RZSWITCH // CONTROL</h1>
<div class='sub'>GameManager replacement &bull; hub <span class='mono'>/GameManagerServiceHub</span> &bull; порт <span class='mono'>{s.BoundPort}</span></div>
{adminWarn}{msgDiv}{gmsWhy}
<div class='grid'>
<div class='card'><h2>GAME MANAGER (RAZER)</h2>
<div class='row'><span>{led(gms.Status)} статус</span><b>{esc(gms.Status)}</b></div>
<div class='row'><span>существует</span><b>{gms.Exists}</b></div>
<div class='row'><span>автостарт</span><b>{esc(gms.StartType)}</b></div>
<form method='post' action='/api/gms-disable'><button type='submit'>Выключить GMS</button></form>
<form method='post' action='/api/gms-enable'><button type='submit'>Включить GMS</button></form>
</div>
<div class='card'><h2>RZSWITCH (НАШ)</h2>
<div class='row'><span>{led(own.Status)} статус</span><b>{esc(own.Status)}</b></div>
<div class='row'><span>установлен</span><b>{own.Exists}</b></div>
<div class='row'><span>автостарт</span><b>{esc(own.StartType)}</b></div>
<form method='post' action='/api/own-install'><button type='submit'>Установить сервис</button></form>
<form method='post' action='/api/own-remove'><button type='submit'>Удалить сервис</button></form>
<form method='post' action='/api/own-start'><button type='submit' {gmsBlocked}>Включить наш сервис</button></form>
<form method='post' action='/api/own-stop'><button type='submit'>Выключить наш сервис</button></form>
</div>
<div class='card'><h2>ДЕТЕКТ</h2>
<div class='row'><span>игр в списке</span><b>{s.Games.Count}</b></div>
<div class='row'><span>последнее событие</span></div>
<div class='mono'>{esc(s.LastEvent)}</div>
</div></div>
<div class='log' id='log'>загрузка лога…</div>
<script>
async function refresh(){{try{{
const r=await fetch('/api/log');document.getElementById('log').textContent=await r.text();
}}catch(e){{}}}}setInterval(refresh,2000);refresh();
</script></body></html>";
    }
}

// ================= file logger =================
public sealed class FileLogger : ILoggerProvider
{
    private readonly string _path;
    public FileLogger(string path) { _path = path; }
    public ILogger CreateLogger(string name) => new FL(_path, name);
    public void Dispose() { }
    private sealed class FL : ILogger
    {
        private readonly string _path; private readonly string _name;
        public FL(string p, string n) { _path = p; _name = n; }
        public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel l, EventId id, TState s, Exception? e, Func<TState, Exception?, string> f)
        {
            try { File.AppendAllLines(_path, new[] { $"{DateTime.Now:HH:mm:ss} [{l}] {_name}: {f(s, e)}" }); } catch { }
        }
    }
}

public static class Program
{
    private static string LogPath() => Path.Combine(AppContext.BaseDirectory, "rzswitch.log");

    [SupportedOSPlatform("windows")]
    public static async Task Main(string[] args)
    {
        // foreground tracker mode: runs in user session, reports active window to the service
        if (args.Any(a => a.Equals("--foreground-tracker", StringComparison.OrdinalIgnoreCase)))
        {
            await TrackerLoop(LogPath());
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
#if DEBUG
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLogger(LogPath()));
#else
        builder.Logging.ClearProviders();
#endif
        builder.Services.AddCors(o => o.AddPolicy("CorsPolicy", p => p
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
        builder.Services.AddSignalR(o =>
        {
#if DEBUG
            o.EnableDetailedErrors = true;
#endif
            o.MaximumReceiveMessageSize = 1024 * 1024;
        }).AddJsonProtocol(o =>
        {
            // Razer's own hub uses JsonStringEnumConverter, so enums (e.g.
            // GameScannerErrorCode) must travel as strings like "GS_SUCCESS".
            o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Use source-generated serialization context for trimming
            o.PayloadSerializerOptions.TypeInfoResolver = RzJsonContext.Default;
        });
        // Configure minimal API JSON serialization for trimming
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.TypeInfoResolver = RzJsonContext.Default;
        });
        builder.Services.AddSingleton<RzState>();
        builder.Host.UseWindowsService();

        var app = builder.Build();
        app.UseCors("CorsPolicy");

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("rzswitch");
        var state = app.Services.GetRequiredService<RzState>();
        var hub = app.Services.GetRequiredService<IHubContext<RzHub, IRzPush>>();
        state.Games = GameList.Load(log);

        // Track FileSystemWatchers for disposal on shutdown
        var watchers = new List<FileSystemWatcher>();

        foreach (var dir in Directory.GetDirectories(@"C:\ProgramData\Razer\GameManager3\Data", "rzr_*"))
        {
            var w = new FileSystemWatcher(dir, "UserGameList_*.xml")
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            w.Changed += (_, _) =>
            {
                Thread.Sleep(500);
                state.Games = GameList.Load(log);
                state.LastEvent = $"перезагружен список игр: {state.Games.Count}";
                log.LogInformation("game list reloaded: {n}", state.Games.Count);
            };
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }

        MapUi(app, state, log, hub);

        // port: explicit arg wins, then registry (GameManager's ServerPort), then dynamic
        int port = 0;
        if (args.Length > 0 && int.TryParse(args[0], out var ap)) port = ap;
        if (port == 0)
        {
            foreach (var key in new[] { @"Software\Razer\Services\GMS3", @"Software\WOW6432Node\Razer\Services\GMS3" })
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    var v = k?.GetValue("ServerPort")?.ToString();
                    if (int.TryParse(v, out var rp) && rp > 0) { port = rp; break; }
                }
                catch { }
            }
        }
        if (port > 0 && !IsFree(port))
        {
            log.LogWarning("port {p} busy, falling back to dynamic", port);
            port = 0;
        }

        foreach (var key in new[] { @"Software\Razer\Services\GMS3", @"Software\WOW6432Node\Razer\Services\GMS3" })
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                var v = k?.GetValue("version")?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) { state.ServiceVersion = v!; break; }
            }
            catch { }
        }

        // dynamic loopback port like stock GMS, then publish to registry
        app.Urls.Clear();
        app.Urls.Add(port > 0 ? $"http://127.0.0.1:{port}" : "http://127.0.0.1:0");
        await app.StartAsync();
        var addr = app.Urls.FirstOrDefault() ?? "";
        port = 0;
        var m = System.Text.RegularExpressions.Regex.Match(addr, @":(\d+)$");
        if (m.Success) port = int.Parse(m.Groups[1].Value);
        state.BoundPort = port;
        WriteServerPort(port, log);
        log.LogInformation("hub on 127.0.0.1:{p}, games: {n}", port, state.Games.Count);

        // Register shutdown callback to dispose watchers
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            watchers.Clear();
        });

        _ = GuardLoop(app, state, log);
        await app.WaitForShutdownAsync();
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var t = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            t.Start();
            t.Stop();
            return true;
        }
        catch { return false; }
    }

    private static void WriteServerPort(int port, ILogger log)
    {
        foreach (var baseKey in new[] { @"Software\Razer\Services\GMS3", @"Software\WOW6432Node\Razer\Services\GMS3" })
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(baseKey);
                k?.SetValue("ServerPort", port.ToString(), Microsoft.Win32.RegistryValueKind.String);
                // read back to verify
                var readBack = k?.GetValue("ServerPort")?.ToString();
                log.LogInformation("ServerPort={p} -> HKLM\\{k} (readback={rb})", port, baseKey, readBack);
            }
            catch (Exception ex) { log.LogWarning("registry write failed for {k} (need admin): {m}", baseKey, ex.Message); }
        }
    }

    private static void MapUi(WebApplication app, RzState state, ILogger log, IHubContext<RzHub, IRzPush> hub)
    {
        string StatusHtml(string msg)
        {
            var gms = Svc.Query(Svc.Gms);
            var own = Svc.Query(Svc.Own);
            return Ui.Page(state, gms, own, Svc.IsAdmin(), msg ?? "");
        }

        app.MapGet("/", (RzState s) => Results.Content(StatusHtml(""), "text/html; charset=utf-8"));

        app.MapGet("/api/log", () =>
        {
            try
            {
                var lines = File.ReadAllLines(LogPath());
                return Results.Text(string.Join('\n', lines.TakeLast(40)), "text/plain; charset=utf-8");
            }
            catch (Exception ex) { return Results.Text("log: " + ex.Message); }
        });

        app.MapGet("/api/port", () => Results.Text(state.BoundPort.ToString(), "text/plain; charset=utf-8"));

        app.MapPost("/api/foreground", async (ForegroundUpdate update) =>
        {
            await HandleForegroundChange(update.Pid, update.Exe, state, hub, log);
            return Results.Ok();
        });

        app.MapHub<RzHub>("/GameManagerServiceHub");

        string Do(string label, Func<string> act)
        {
            string outp;
            try { outp = act(); }
            catch (Exception ex) { outp = "ERR: " + ex.Message; }
            log.LogInformation("{l}: {o}", label, outp.Split('\n')[0]);
            return StatusHtml(label + ":\n" + outp);
        }

        app.MapPost("/api/gms-disable", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"stop \"{Svc.Gms}\"");
            var (o2, c2) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= disabled");
            return Results.Content(Do("GMS выключен", () => $"stop[{c1}]\n{o1}\nconfig[{c2}]\n{o2}"), "text/html; charset=utf-8");
        });
        app.MapPost("/api/gms-enable", () =>
        {
            var (o1, c1) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= demand");
            var (o2, c2) = Svc.Run("sc", $"start \"{Svc.Gms}\"");
            return Results.Content(Do("GMS включен", () => $"config[{c1}]\n{o1}\nstart[{c2}]\n{o2}"), "text/html; charset=utf-8");
        });
        app.MapPost("/api/own-install", () =>
            Results.Content(Do("rzswitch установлен", () =>
            {
                var exe = Svc.ExePath();
                var (o1, c1) = Svc.Run("sc", $"create {Svc.Own} binPath= \"{exe}\" start= auto");
                var (o2, c2) = Svc.Run("sc", $"description {Svc.Own} \"Razer GameManager replacement: foreground detector + SignalR hub for Synapse profiles\"");
                RegisterTrackerAutostart(log);
                StartTracker(log);
                return $"create[{c1}]\n{o1}\ndesc[{c2}]\n{o2}";
            }), "text/html; charset=utf-8"));
        app.MapPost("/api/own-remove", () =>
            Results.Content(Do("rzswitch удален", () =>
            {
                var (o1, c1) = Svc.Run("sc", $"stop {Svc.Own}");
                var (o2, c2) = Svc.Run("sc", $"delete {Svc.Own}");
                return $"stop[{c1}]\n{o1}\ndelete[{c2}]\n{o2}";
            }), "text/html; charset=utf-8"));
        app.MapPost("/api/own-start", () =>
        {
            var gms = Svc.Query(Svc.Gms);
            if (gms.Exists && (gms.Status == "Running" || gms.StartType != "disabled"))
                return Results.Content(StatusHtml("Отказ: GameManager активен — сначала выключите его (конфликт порта/реестра)."), "text/html; charset=utf-8");
            return Results.Content(Do("rzswitch запущен", () =>
            {
                var (o, c) = Svc.Run("sc", $"start {Svc.Own}");
                return $"start[{c}]\n{o}";
            }), "text/html; charset=utf-8");
        });
        app.MapPost("/api/own-stop", () =>
            Results.Content(Do("rzswitch остановлен", () =>
            {
                var (o, c) = Svc.Run("sc", $"stop {Svc.Own}");
                UnregisterTrackerAutostart(log);
                return $"stop[{c}]\n{o}";
            }), "text/html; charset=utf-8"));
    }

    // GMS guard: every 30s a sub-millisecond SCM check; if Razer restarted
    // GameManager, stop it, pin it to disabled and take ServerPort back.
    private static async Task GuardLoop(WebApplication app, RzState state, ILogger log)
    {
        static string First(string s) => s.Split('\n').FirstOrDefault()?.Trim() ?? "";
        var stop = app.Lifetime.ApplicationStopping;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (GmsGuard.IsActive(Svc.Gms, out var st))
                {
                    log.LogWarning("GMS guard: GameManager active (state={s}), stopping", st);
                    var (o1, c1) = Svc.Run("sc", $"stop \"{Svc.Gms}\"");
                    var (o2, c2) = Svc.Run("sc", $"config \"{Svc.Gms}\" start= disabled");
                    WriteServerPort(state.BoundPort, log);
                    state.LastEvent = $"GMS guard: stopped GameManager (stop[{c1}] config[{c2}])";
                    log.LogWarning("{e}: {o1} | {o2}", state.LastEvent, First(o1), First(o2));
                }
                // registry is touched only when GMS actually started itself (see branch above)
            }
            catch (Exception ex) { log.LogWarning("guard: {m}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch { break; }
        }
    }

    // shared state mutation lock (/api/foreground, GuardLoop)
    private static readonly object StateLock = new();

    private static async Task HandleForegroundChange(uint pid, string exe, RzState state,
        IHubContext<RzHub, IRzPush> hub, ILogger log)
    {
        if (string.IsNullOrEmpty(exe)) return;
        lock (StateLock)
        {
            if (!string.Equals(exe, state.CurrentExe, StringComparison.OrdinalIgnoreCase))
            {
                if (state.CurrentExe != null && state.Games.TryGetValue(state.CurrentExe, out var prev))
                {
                    var prevCopy = new GameSignature
                    {
                        EntryGuid = prev.EntryGuid,
                        DisplayName = prev.DisplayName,
                        GameTitle = prev.GameTitle,
                        LaunchExe = prev.LaunchExe,
                        ProcessName = state.CurrentExe,
                        DefaultExe = prev.DefaultExe,
                        DefaultExe32 = prev.DefaultExe32,
                        DefaultExe64 = prev.DefaultExe64,
                    };
                    var duration = state.CurrentStartedAt == default
                        ? TimeSpan.Zero
                        : DateTime.Now - state.CurrentStartedAt;

                    var stopped = new GameSignatureEventArgs { GameSignature = prevCopy };
                    var stoppedPid = new GameSignatureWithPidEventArgs
                    {
                        GameSignatureWithPid = new GameSignatureWithPid { Pid = state.CurrentPid, Signature = prevCopy },
                    };
                    var stoppedDuration = new GameSignatureWithDurationEventArgs
                    {
                        GameSignatureWithDuration = new GameSignatureWithDuration { Duration = duration, Signature = prevCopy },
                    };

                    _ = hub.Clients.All.OnGameStoppedEvent(stopped);
                    _ = hub.Clients.All.OnGameStoppedWithPidEvent(stoppedPid);
                    _ = hub.Clients.All.OnGameStoppedWithDurationEvent(stoppedDuration);
                    state.LastEvent = $"STOP {state.CurrentExe} pid={state.CurrentPid}";
                    log.LogInformation("{e} -> OnGameStoppedEvent payload={payload}", state.LastEvent, JsonSerializer.Serialize(stopped));
                }
                state.CurrentExe = exe;
                state.CurrentPid = pid;
                state.CurrentStartedAt = DateTime.Now;
                if (state.Games.TryGetValue(exe, out var sig))
                {
                    var started = new GameSignatureEventArgs { GameSignature = sig };
                    var startedPid = new GameSignatureWithPidEventArgs
                    {
                        GameSignatureWithPid = new GameSignatureWithPid { Pid = pid, Signature = sig },
                    };
                    _ = hub.Clients.All.OnGameStartedEvent(started);
                    _ = hub.Clients.All.OnGameStartedWithPidEvent(startedPid);
                    state.LastEvent = $"START {exe} pid={pid} guid={sig.EntryGuid}";
                    log.LogInformation("{e} -> OnGameStartedEvent payload={payload}", state.LastEvent, JsonSerializer.Serialize(started));
                }
                else
                {
                    state.LastEvent = $"{DateTime.Now:HH:mm:ss} foreground: {exe} (не игра)";
                    log.LogInformation("{e}", state.LastEvent);
                }
            }
        }
    }

    // user-session foreground tracker: runs as rzswitch.exe --foreground-tracker
    // (autostart via HKCU Run). Calls GetForegroundWindow in the user session and
    // POSTs changes to the service's /api/foreground endpoint.
    private static async Task TrackerLoop(string logPath)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var log = new FileLogger(logPath).CreateLogger("tracker");
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        int port = 0;
        string? lastExe = null;

        while (!cts.Token.IsCancellationRequested)
        {
            if (port == 0)
            {
                port = ReadServerPortFromRegistry();
                if (port == 0 || port == 14914)
                {
                    // try fallback ports
                    foreach (var p2 in new[] { 1654, 14915, 5000 })
                    {
                        try
                        {
                            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                            var r = await hc.GetAsync($"http://127.0.0.1:{p2}/api/log");
                            if (r.IsSuccessStatusCode) { port = p2; break; }
                        }
                        catch { }
                    }
                }
                if (port == 0)
                {
                    log.LogInformation("tracker: waiting for service port...");
                    try { await Task.Delay(2000, cts.Token); } catch { break; }
                    continue;
                }
                log.LogInformation("tracker: service port={p}", port);
            }

            try
            {
                var (pid, exe) = Foreground.Get();
                if (!string.Equals(exe, lastExe, StringComparison.OrdinalIgnoreCase))
                {
                    lastExe = exe;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        try
                        {
                            var payload = new { pid, exe };
                            var json = JsonSerializer.Serialize(payload);
                            var content = new StringContent(json, Encoding.UTF8, "application/json");
                            var resp = await http.PostAsync(
                                $"http://127.0.0.1:{port}/api/foreground", content, cts.Token);
                            log.LogInformation("tracker: exe={exe} pid={pid} -> HTTP {status}",
                                exe, pid, (int)resp.StatusCode);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            port = 0; // service not ready / port changed, re-read
                            log.LogWarning("tracker: cannot reach service: {m}", ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex) { log.LogWarning("tracker: {m}", ex.Message); }

            try { await Task.Delay(500, cts.Token); } catch { break; }
        }
    }

    private static int ReadServerPortFromRegistry()
    {
        foreach (var key in new[] { @"Software\Razer\Services\GMS3", @"Software\WOW6432Node\Razer\Services\GMS3" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                var v = k?.GetValue("ServerPort")?.ToString();
                if (int.TryParse(v, out var p) && p > 0) return p;
            }
            catch { }
        }
        return 0;
    }

    private const string TrackerRunValue = "rzswitch-tracker";

    private static string TrackerExePath() => Path.Combine(AppContext.BaseDirectory, "rsztracker.exe");

    private static void RegisterTrackerAutostart(ILogger log)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            k?.SetValue(TrackerRunValue, $"\"{TrackerExePath()}\"", RegistryValueKind.String);
            log.LogInformation("tracker autostart registered: {path}", TrackerExePath());
        }
        catch (Exception ex) { log.LogWarning("tracker autostart register failed: {m}", ex.Message); }
    }

    private static void UnregisterTrackerAutostart(ILogger log)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            k?.DeleteValue(TrackerRunValue, throwOnMissingValue: false);
            log.LogInformation("tracker autostart unregistered");
        }
        catch (Exception ex) { log.LogWarning("tracker autostart unregister failed: {m}", ex.Message); }
    }

    private static void StartTracker(ILogger log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(TrackerExePath())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            log.LogInformation("tracker started (hidden): {path}", TrackerExePath());
        }
        catch (Exception ex) { log.LogWarning("tracker start failed: {m}", ex.Message); }
    }
}