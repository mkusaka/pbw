using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pbw.Core;

public static class PbwSchema
{
    public const string Version = "pbw.stable.v1";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed record Bounds(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

public sealed record DisplayContext(string Id, Bounds Bounds, double Scale = 1, bool Primary = true);

public sealed record TargetSpec(
    string? Id = null,
    string? Text = null,
    string? Role = null,
    string? AutomationId = null,
    int? X = null,
    int? Y = null,
    int? WindowHandle = null,
    int? Index = null)
{
    public static TargetSpec FromArgs(IReadOnlyList<string> args)
    {
        var map = ArgParser.ParseOptions(args);
        return new TargetSpec(
            map.GetValueOrDefault("id"),
            map.GetValueOrDefault("text"),
            map.GetValueOrDefault("role"),
            map.GetValueOrDefault("automation-id"),
            ArgParser.Int(map, "x"),
            ArgParser.Int(map, "y"),
            ArgParser.Int(map, "hwnd"),
            ArgParser.Int(map, "index"));
    }
}

public sealed record ElementSnapshot(
    string Id,
    string? Name,
    string Role,
    Bounds Bounds,
    string? AutomationId = null,
    bool Enabled = true,
    bool Focused = false,
    IReadOnlyList<string>? Patterns = null,
    IReadOnlyList<ElementSnapshot>? Children = null);

public sealed record OcrTextSnapshot(string Text, Bounds Bounds, double Confidence = 0);

public sealed record WindowSnapshot(
    int Handle,
    string Title,
    string ProcessName,
    Bounds Bounds,
    bool IsVisible = true,
    bool IsMinimized = false,
    bool IsForeground = false);

public sealed record Snapshot(
    string SchemaVersion,
    string Id,
    DateTimeOffset CreatedAt,
    DisplayContext Display,
    IReadOnlyList<WindowSnapshot> Windows,
    IReadOnlyList<ElementSnapshot> Elements,
    IReadOnlyList<OcrTextSnapshot> OcrText,
    string? ImagePath = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public static Snapshot Empty(string id) => new(
        PbwSchema.Version,
        id,
        DateTimeOffset.UtcNow,
        new DisplayContext("primary", new Bounds(0, 0, 0, 0)),
        Array.Empty<WindowSnapshot>(),
        Array.Empty<ElementSnapshot>(),
        Array.Empty<OcrTextSnapshot>());
}

public sealed record PbwError(
    string Code,
    string Message,
    string? Target = null,
    IReadOnlyDictionary<string, object?>? Details = null,
    string? RetryHint = null);

public sealed record PbwEnvelope<T>(
    string SchemaVersion,
    bool Ok,
    T? Data = default,
    PbwError? Error = null)
{
    public static PbwEnvelope<T> Success(T data) => new(PbwSchema.Version, true, data);
    public static PbwEnvelope<T> Failure(PbwError error) => new(PbwSchema.Version, false, default, error);
}

public sealed record ActionResult(
    string Action,
    bool Performed,
    string Method,
    string? TargetId = null,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? Details = null);

public sealed record CommandResult(int ExitCode, string Json);

public sealed record PbwConfig(
    string SchemaVersion,
    string SnapshotDirectory,
    int SnapshotRetentionDays,
    int MaxSnapshots,
    SafetyConfig Safety,
    RedactionConfig Redaction,
    McpConfig Mcp)
{
    public static PbwConfig Defaults(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pbw");
        return new PbwConfig(
            PbwSchema.Version,
            Path.Combine(root, "snapshots"),
            14,
            100,
            new SafetyConfig(true, true, Array.Empty<string>(), Array.Empty<string>()),
            new RedactionConfig(true, new[] { "password", "token", "secret" }),
            new McpConfig(true, false));
    }
}

public sealed record SafetyConfig(
    bool LocalOnly,
    bool ConfirmDestructiveActions,
    IReadOnlyList<string> AllowTools,
    IReadOnlyList<string> DenyTools);

public sealed record RedactionConfig(bool Enabled, IReadOnlyList<string> TextPatterns);

public sealed record McpConfig(bool StdioEnabled, bool RemoteListenerEnabled);

public sealed class ConfigLoader
{
    public string ConfigPath { get; }

    public ConfigLoader(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "pbw",
            "config.json");
    }

    public PbwConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return PbwConfig.Defaults();
        }

        try
        {
            var config = JsonSerializer.Deserialize<PbwConfig>(File.ReadAllText(ConfigPath), PbwSchema.Json);
            return Validate(config ?? PbwConfig.Defaults()).Config;
        }
        catch (Exception ex)
        {
            throw new PbwException(ErrorMapper.Map(ex, "config"));
        }
    }

    public void Save(PbwConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, PbwSchema.Json));
    }

    public ConfigValidationResult Validate(PbwConfig config)
    {
        var errors = new List<string>();
        if (config.SchemaVersion != PbwSchema.Version) errors.Add("schemaVersion must be pbw.stable.v1");
        if (string.IsNullOrWhiteSpace(config.SnapshotDirectory)) errors.Add("snapshotDirectory is required");
        if (config.SnapshotRetentionDays < 0) errors.Add("snapshotRetentionDays must be non-negative");
        if (config.MaxSnapshots < 1) errors.Add("maxSnapshots must be positive");
        if (config.Mcp.RemoteListenerEnabled) errors.Add("mcp.remoteListenerEnabled is not allowed in v1");
        return new ConfigValidationResult(errors.Count == 0, errors, config);
    }
}

public sealed record ConfigValidationResult(bool Valid, IReadOnlyList<string> Errors, PbwConfig Config);

public sealed class PbwException(PbwError error) : Exception(error.Message)
{
    public PbwError Error { get; } = error;
}

public static class ErrorMapper
{
    public static PbwError Map(Exception ex, string? target = null) => ex switch
    {
        PbwException pbw => pbw.Error,
        UnauthorizedAccessException => new PbwError("access_denied", ex.Message, target, RetryHint: "Run with appropriate permissions or choose a writable path."),
        DirectoryNotFoundException => new PbwError("not_found", ex.Message, target),
        FileNotFoundException => new PbwError("not_found", ex.Message, target),
        ArgumentException => new PbwError("invalid_argument", ex.Message, target),
        InvalidOperationException => new PbwError("invalid_state", ex.Message, target),
        PlatformNotSupportedException => new PbwError("unsupported_platform", ex.Message, target),
        _ => new PbwError("internal_error", ex.Message, target)
    };
}

public interface ISnapshotSource
{
    Task<Snapshot> CaptureAsync(CancellationToken cancellationToken);
}

public interface IWindowService
{
    IReadOnlyList<WindowSnapshot> ListWindows();
    ActionResult Focus(int handle);
    ActionResult Move(int handle, int x, int y);
    ActionResult Resize(int handle, int width, int height);
    ActionResult SetBounds(int handle, Bounds bounds);
    ActionResult Minimize(int handle);
    ActionResult Maximize(int handle);
    ActionResult Restore(int handle);
    ActionResult Close(int handle);
}

public interface IAppService
{
    IReadOnlyList<AppInfo> ListApps();
    ActionResult Launch(string path, string? arguments);
    ActionResult Focus(string processName);
    ActionResult Switch(string processName);
    ActionResult Quit(string processName);
}

public sealed record AppInfo(int ProcessId, string ProcessName, string? MainWindowTitle, int? MainWindowHandle);

public interface IClipboardService
{
    string? GetText();
    ActionResult SetText(string text);
    ActionResult Clear();
}

public interface IInputService
{
    ActionResult Click(int x, int y, string button = "left");
    ActionResult Move(int x, int y);
    ActionResult TypeText(string text);
    ActionResult Press(string key);
    ActionResult Hotkey(IReadOnlyList<string> keys);
    ActionResult Scroll(int delta, int? x = null, int? y = null);
    ActionResult Drag(int fromX, int fromY, int toX, int toY);
}

public interface IElementAutomationService
{
    IReadOnlyList<ElementSnapshot> ReadTree();
    ActionResult SetValue(TargetSpec target, string value);
    ActionResult PerformAction(TargetSpec target, string action);
    IReadOnlyList<MenuItemInfo> ListMenus(TargetSpec target);
    ActionResult ClickMenu(TargetSpec target, string menu);
    IReadOnlyList<DialogInfo> ListDialogs();
    ActionResult ClickDialog(TargetSpec target, string button);
    ActionResult InputDialog(TargetSpec target, string value);
    ActionResult DismissDialog(TargetSpec target);
}

public sealed record MenuItemInfo(string Text, bool Enabled = true, string? Id = null);
public sealed record DialogInfo(string Id, string Title, IReadOnlyList<ElementSnapshot> Controls);

public interface IDoctorCheckService
{
    IReadOnlyList<DoctorCheck> RunChecks(PbwConfig config);
}

public sealed record DoctorCheck(string Name, string Status, string Message, IReadOnlyDictionary<string, object?>? Details = null);

public sealed class SnapshotStore
{
    private readonly string directory;

    public SnapshotStore(string directory)
    {
        this.directory = directory;
    }

    public string DirectoryPath => directory;

    public string Save(Snapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, snapshot.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, PbwSchema.Json));
        return path;
    }

    public IReadOnlyList<SnapshotSummary> List()
    {
        if (!Directory.Exists(directory)) return Array.Empty<SnapshotSummary>();
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    return new SnapshotSummary(
                        root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(path),
                        root.GetProperty("createdAt").GetDateTimeOffset(),
                        path);
                }
                catch
                {
                    return new SnapshotSummary(Path.GetFileNameWithoutExtension(path), File.GetLastWriteTimeUtc(path), path);
                }
            })
            .OrderByDescending(s => s.CreatedAt)
            .ToArray();
    }

    public Snapshot Show(string id)
    {
        var path = Path.Combine(directory, id + ".json");
        if (!File.Exists(path)) throw new PbwException(new PbwError("snapshot_not_found", $"Snapshot '{id}' was not found.", id));
        return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), PbwSchema.Json)
            ?? throw new PbwException(new PbwError("invalid_snapshot", $"Snapshot '{id}' is not valid JSON.", id));
    }

    public InspectResult Inspect(string id, TargetSpec target)
    {
        var snapshot = Show(id);
        var element = new ElementMatcher().Find(snapshot.Elements, target);
        return new InspectResult(id, element, element is null ? "not_found" : "matched");
    }

    public CleanResult Clean(int retentionDays, int maxSnapshots)
    {
        if (!Directory.Exists(directory)) return new CleanResult(0, Array.Empty<string>());
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var all = List();
        var delete = all.Where(s => s.CreatedAt < cutoff)
            .Concat(all.Skip(maxSnapshots))
            .Select(s => s.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in delete) File.Delete(path);
        return new CleanResult(delete.Length, delete);
    }
}

public sealed record SnapshotSummary(string Id, DateTimeOffset CreatedAt, string Path);
public sealed record InspectResult(string SnapshotId, ElementSnapshot? Element, string Status);
public sealed record CleanResult(int Deleted, IReadOnlyList<string> Paths);

public sealed class ElementMatcher
{
    public ElementSnapshot? Find(IEnumerable<ElementSnapshot> roots, TargetSpec target)
    {
        var flattened = Flatten(roots).ToArray();
        if (target.Id is not null) return flattened.FirstOrDefault(e => e.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        if (target.AutomationId is not null) return flattened.FirstOrDefault(e => (e.AutomationId ?? "").Equals(target.AutomationId, StringComparison.OrdinalIgnoreCase));
        if (target.Text is not null)
        {
            var rx = new Regex(Regex.Escape(target.Text), RegexOptions.IgnoreCase);
            return flattened.FirstOrDefault(e => e.Name is not null && rx.IsMatch(e.Name));
        }
        if (target.Role is not null) return flattened.FirstOrDefault(e => e.Role.Equals(target.Role, StringComparison.OrdinalIgnoreCase));
        if (target.X is not null && target.Y is not null)
        {
            return flattened.Where(e =>
                target.X >= e.Bounds.X && target.X <= e.Bounds.X + e.Bounds.Width &&
                target.Y >= e.Bounds.Y && target.Y <= e.Bounds.Y + e.Bounds.Height)
                .OrderBy(e => e.Bounds.Width * e.Bounds.Height)
                .FirstOrDefault();
        }
        if (target.Index is not null && target.Index.Value >= 0 && target.Index.Value < flattened.Length) return flattened[target.Index.Value];
        return flattened.FirstOrDefault();
    }

    private static IEnumerable<ElementSnapshot> Flatten(IEnumerable<ElementSnapshot> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            if (root.Children is null) continue;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }
}

public sealed class TargetResolver(ISnapshotSource source)
{
    private readonly ElementMatcher matcher = new();

    public async Task<ElementSnapshot?> ResolveAsync(TargetSpec target, CancellationToken cancellationToken = default)
    {
        var snapshot = await source.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return matcher.Find(snapshot.Elements, target);
    }
}

public sealed class CoordinateConverter
{
    public (int PhysicalX, int PhysicalY) ToPhysical(int logicalX, int logicalY, DisplayContext display) =>
        ((int)Math.Round((logicalX - display.Bounds.X) * display.Scale + display.Bounds.X),
         (int)Math.Round((logicalY - display.Bounds.Y) * display.Scale + display.Bounds.Y));

    public (int LogicalX, int LogicalY) ToLogical(int physicalX, int physicalY, DisplayContext display)
    {
        var scale = display.Scale == 0 ? 1 : display.Scale;
        return ((int)Math.Round((physicalX - display.Bounds.X) / scale + display.Bounds.X),
            (int)Math.Round((physicalY - display.Bounds.Y) / scale + display.Bounds.Y));
    }
}

public sealed class ActionRouter(
    IInputService input,
    IElementAutomationService automation,
    ISnapshotSource snapshotSource)
{
    public async Task<ActionResult> ClickAsync(TargetSpec target, CancellationToken cancellationToken = default)
    {
        if (target.X is not null && target.Y is not null) return input.Click(target.X.Value, target.Y.Value);
        var element = await new TargetResolver(snapshotSource).ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (element is null) throw new PbwException(new PbwError("target_not_found", "No element matched the target.", target.ToString()));
        if (element.Patterns?.Any(p => p.Equals("Invoke", StringComparison.OrdinalIgnoreCase)) == true)
            return automation.PerformAction(target, "invoke");
        return input.Click(element.Bounds.CenterX, element.Bounds.CenterY);
    }

    public ActionResult SetValue(TargetSpec target, string value) => automation.SetValue(target, value);
    public ActionResult PerformAction(TargetSpec target, string action) => automation.PerformAction(target, action);
}

public static class ArgParser
{
    public static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = arg[2..];
            var value = "true";
            var equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            result[key] = value;
        }
        return result;
    }

    public static int? Int(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && int.TryParse(value, out var i) ? i : null;
}
