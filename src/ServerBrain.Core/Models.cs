using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerBrain;

/// <summary>How a project is started.</summary>
public enum RunMode
{
    /// <summary>Started by hand from the dashboard; runs until stopped ("once").</summary>
    Manual,
    /// <summary>Started automatically with Server Brain (i.e. with Windows) and kept alive.</summary>
    Always,
    /// <summary>Started the first time its URL is opened; stopped again after being idle.</summary>
    OnDemand,
}

public enum RestartPolicy { Never, OnFailure, Always }

public enum ProjectStatus { Stopped, Starting, Running, Stopping, Crashed, Failed, Blocked, Waiting }

public enum ShareMode { Off, Quick, Named }

public sealed class ShareConfig
{
    public ShareMode Mode { get; set; } = ShareMode.Off;
    /// <summary>Public hostname of a named Cloudflare tunnel (display + routing only).</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Encrypted Cloudflare tunnel token. Never sent to the dashboard.</summary>
    public string? TokenEnc { get; set; }
    public bool HasToken => !string.IsNullOrEmpty(TokenEnc);
    /// <summary>Open the tunnel automatically whenever Server Brain starts.</summary>
    public bool AutoStart { get; set; }
    /// <summary>Public visitors must enter an access code before they reach the project.</summary>
    public bool RequireCode { get; set; } = true;
    public string Code { get; set; } = "";
}

public class ProjectConfig
{
    /// <summary>URL-safe slug. Also the local hostname: http://{id}.localhost</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>node | python | php | docker | static | custom (cosmetic, drives the icon)</summary>
    public string Type { get; set; } = "custom";
    public string Command { get; set; } = "";
    /// <summary>Optional command run before the process tree is killed (e.g. "docker compose down").</summary>
    public string StopCommand { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = new();

    public int? Port { get; set; }
    public List<int> ExtraPorts { get; set; } = new();
    /// <summary>When set, the primary port is also passed to the project in this environment variable.</summary>
    public bool InjectPort { get; set; }
    public string PortEnvName { get; set; } = "PORT";
    public string UrlPath { get; set; } = "/";

    public RunMode Mode { get; set; } = RunMode.Manual;
    public int IdleMinutes { get; set; } = 15;
    public RestartPolicy Restart { get; set; } = RestartPolicy.OnFailure;
    public int MaxRestarts { get; set; } = 5;
    /// <summary>idle | belownormal | normal</summary>
    public string Priority { get; set; } = "normal";
    public int MemoryLimitMb { get; set; }
    public int StartDelaySec { get; set; }
    public int StartupTimeoutSec { get; set; } = 90;
    public bool KeepStdinOpen { get; set; }
    public bool OpenBrowserOnStart { get; set; }
    /// <summary>Rewrite Origin/Referer/Location headers when proxying under the friendly name.</summary>
    public bool RewriteHeaders { get; set; } = true;

    public string Group { get; set; } = "";
    public string Color { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> DependsOn { get; set; } = new();
    public int Order { get; set; }

    public ShareConfig Share { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<int> AllPorts
    {
        get
        {
            if (Port is int p) yield return p;
            foreach (var e in ExtraPorts) if (e != Port) yield return e;
        }
    }

    public ProjectConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, Json.Options);
        return JsonSerializer.Deserialize<ProjectConfig>(json, Json.Options)!;
    }

    /// <summary>Copy that is safe to send to the dashboard (secrets removed).</summary>
    public ProjectConfig ToPublic()
    {
        var c = Clone();
        c.Share.TokenEnc = null;
        return c;
    }
}

/// <summary>Incoming create/update payload. <see cref="ShareToken"/> is the plain Cloudflare token (empty = keep).</summary>
public sealed class ProjectInput : ProjectConfig
{
    public string? ShareToken { get; set; }
    public bool ClearShareToken { get; set; }
}

public sealed class AppSettings
{
    public int DashboardPort { get; set; } = 7777;
    public bool ProxyEnabled { get; set; } = true;
    public int ProxyPort { get; set; } = 80;
    public int StaggerSeconds { get; set; } = 3;
    public bool Notifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ConfirmExit { get; set; } = true;
    public bool AutoOpenBrowser { get; set; } = true;
    public int DefaultIdleMinutes { get; set; } = 15;
    public string CloudflaredPath { get; set; } = "";
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "violet";
    public bool ReduceMotion { get; set; }
    public bool LowPowerUi { get; set; }
    /// <summary>The first-run welcome has been completed (or skipped).</summary>
    public bool OnboardingDone { get; set; }
}

public sealed class StateFile
{
    public int Version { get; set; } = 1;
    public List<ProjectConfig> Projects { get; set; } = new();
}

public sealed record ConflictInfo(
    int Port,
    string Kind,          // duplicate | in-use | reserved | mismatch
    string Severity,      // warning | error
    string Message,
    string? OtherProjectId = null,
    int? Pid = null,
    string? ProcessName = null);

public sealed class TunnelInfo
{
    public string Status { get; set; } = "off";   // off | starting | connected | error
    public string? Url { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectRuntime
{
    public ProjectStatus Status { get; set; }
    public int? Pid { get; set; }
    public int ProcessCount { get; set; }
    public long? StartedAt { get; set; }
    public int? EffectivePort { get; set; }
    public int? DetectedPort { get; set; }
    public int RestartCount { get; set; }
    public double Cpu { get; set; }
    public double MemMb { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    /// <summary>Id of a tool (node, python…) this project needs that is not installed.</summary>
    public string? MissingTool { get; set; }
    public string? MissingToolName { get; set; }
    public long? NextRestartAt { get; set; }
    public long? LastActivity { get; set; }
    public string? LocalUrl { get; set; }
    public string? DirectUrl { get; set; }
    public List<ConflictInfo> Conflicts { get; set; } = new();
    public TunnelInfo Tunnel { get; set; } = new();
}

public sealed class ProjectView
{
    public ProjectConfig Config { get; set; } = new();
    public ProjectRuntime Runtime { get; set; } = new();
}

public sealed class ActivityEntry
{
    public long Ts { get; set; }
    public string Level { get; set; } = "info";   // info | ok | warn | error
    public string Kind { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string Message { get; set; } = "";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
