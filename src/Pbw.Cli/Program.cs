using System.Text.Json;
using Pbw.Core;
using Pbw.Mcp;
using Pbw.Windows;

namespace Pbw.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var app = CreateDefaultApp();
        var result = await app.ExecuteAsync(args, CancellationToken.None).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.Json))
        {
            Console.Out.WriteLine(result.Json);
        }
        return result.ExitCode;
    }

    public static PbwCli CreateDefaultApp()
    {
        var configLoader = new ConfigLoader();
        var config = configLoader.Load();
        var windows = new WindowsWindowService();
        var automation = new WindowsElementAutomationService();
        var snapshot = new WindowsSnapshotSource(windows, automation, new WindowsCaptureService(), new WindowsOcrService(), config.SnapshotDirectory);
        var store = new SnapshotStore(config.SnapshotDirectory);
        var input = new WindowsInputService();
        var router = new ActionRouter(input, automation, snapshot);
        var clipboard = new WindowsClipboardService();
        var apps = new WindowsAppService(windows);
        var doctor = new WindowsDoctorCheckService();
        return new PbwCli(configLoader, config, snapshot, store, input, router, windows, apps, clipboard, automation, doctor);
    }
}

public sealed class PbwCli(
    ConfigLoader configLoader,
    PbwConfig config,
    ISnapshotSource snapshotSource,
    SnapshotStore snapshotStore,
    IInputService input,
    ActionRouter router,
    IWindowService windows,
    IAppService apps,
    IClipboardService clipboard,
    IElementAutomationService automation,
    IDoctorCheckService doctor) : IPbwCommandExecutor
{
    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
            {
                return Ok(new { commands = Commands.All, usage = "pbw <command> [options]", schemaVersion = PbwSchema.Version });
            }

            var output = await ExecuteObjectAsync(args, cancellationToken).ConfigureAwait(false);
            if (output is NoOutput) return new CommandResult(0, "");
            return Ok(output);
        }
        catch (Exception ex)
        {
            var error = ErrorMapper.Map(ex, args.Count > 0 ? args[0] : null);
            return new CommandResult(error.Code is "invalid_argument" ? 2 : 1, JsonSerializer.Serialize(PbwEnvelope<object?>.Failure(error), PbwSchema.Json));
        }
    }

    public async Task<PbwEnvelope<object?>> ExecuteAsync(string command, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var args = new List<string>();
            args.AddRange(command.Split('.', StringSplitOptions.RemoveEmptyEntries));
            foreach (var (key, value) in arguments)
            {
                args.Add("--" + key.Replace("_", "-"));
                if (value is not null) args.Add(value.ToString()!);
            }
            return PbwEnvelope<object?>.Success(await ExecuteObjectAsync(args, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return PbwEnvelope<object?>.Failure(ErrorMapper.Map(ex, command));
        }
    }

    private async Task<object?> ExecuteObjectAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = args[0];
        var tail = args.Skip(1).ToArray();
        var options = ArgParser.ParseOptions(tail);
        EnforceToolPolicy(command, tail);
        return command switch
        {
            "see" => await See(cancellationToken).ConfigureAwait(false),
            "image" => await Image(cancellationToken).ConfigureAwait(false),
            "click" => await router.ClickAsync(TargetSpec.FromArgs(tail), cancellationToken).ConfigureAwait(false),
            "type" => input.TypeText(Required(options, "text")),
            "press" => input.Press(Required(options, "key")),
            "hotkey" => input.Hotkey((options.GetValueOrDefault("keys") ?? string.Join("+", tail.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))).Split('+', StringSplitOptions.RemoveEmptyEntries)),
            "scroll" => input.Scroll(Int(options, "delta", 120), ArgParser.Int(options, "x"), ArgParser.Int(options, "y")),
            "drag" => input.Drag(Int(options, "from-x"), Int(options, "from-y"), Int(options, "to-x"), Int(options, "to-y")),
            "move" => input.Move(Int(options, "x"), Int(options, "y")),
            "set-value" => router.SetValue(TargetSpec.FromArgs(tail), Required(options, "value")),
            "perform-action" => router.PerformAction(TargetSpec.FromArgs(tail), options.GetValueOrDefault("action") ?? "invoke"),
            "window" => Window(tail, options),
            "app" => App(tail, options),
            "menu" => Menu(tail, options),
            "dialog" => Dialog(tail, options),
            "clipboard" => Clipboard(tail, options),
            "snapshot" => Snapshot(tail, options),
            "config" => Config(tail, options),
            "doctor" => new { checks = doctor.RunChecks(config) },
            "mcp" => await RunMcp(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{command}'.")
        };
    }

    private void EnforceToolPolicy(string command, IReadOnlyList<string> tail)
    {
        var tool = command is "window" or "app" or "menu" or "dialog" or "clipboard" or "snapshot" or "config"
            ? command + "." + (tail.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "")
            : command;
        if (config.Safety.DenyTools.Any(t => t.Equals(tool, StringComparison.OrdinalIgnoreCase) || t.Equals(command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PbwException(new PbwError("tool_denied", $"Tool '{tool}' is denied by configuration.", tool));
        }
        if (config.Safety.AllowTools.Count > 0 &&
            !config.Safety.AllowTools.Any(t => t.Equals(tool, StringComparison.OrdinalIgnoreCase) || t.Equals(command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PbwException(new PbwError("tool_not_allowed", $"Tool '{tool}' is not in the configured allow list.", tool));
        }
    }

    private async Task<object> See(CancellationToken cancellationToken)
    {
        var snapshot = await snapshotSource.CaptureAsync(cancellationToken).ConfigureAwait(false);
        snapshot = new SnapshotRedactor(config.Redaction).Redact(snapshot);
        var path = snapshotStore.Save(snapshot);
        return new { snapshot = snapshot with { Metadata = Merge(snapshot.Metadata, "snapshotPath", path) } };
    }

    private async Task<object> Image(CancellationToken cancellationToken)
    {
        var snapshot = await snapshotSource.CaptureAsync(cancellationToken).ConfigureAwait(false);
        snapshot = new SnapshotRedactor(config.Redaction).Redact(snapshot);
        snapshotStore.Save(snapshot);
        return new { snapshotId = snapshot.Id, imagePath = snapshot.ImagePath, status = snapshot.ImagePath is null ? "degraded" : "ok" };
    }

    private object Window(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "list" => new { windows = windows.ListWindows() },
            "focus" => windows.Focus(Int(options, "hwnd")),
            "move" => windows.Move(Int(options, "hwnd"), Int(options, "x"), Int(options, "y")),
            "resize" => windows.Resize(Int(options, "hwnd"), Int(options, "width"), Int(options, "height")),
            "set-bounds" => windows.SetBounds(Int(options, "hwnd"), Bounds(options)),
            "minimize" => windows.Minimize(Int(options, "hwnd")),
            "maximize" => windows.Maximize(Int(options, "hwnd")),
            "restore" => windows.Restore(Int(options, "hwnd")),
            "close" => ConfirmDestructive(options, "window.close") ?? windows.Close(Int(options, "hwnd")),
            _ => throw new ArgumentException($"Unknown window command '{sub}'.")
        };
    }

    private object App(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "list" => new { apps = apps.ListApps() },
            "launch" => apps.Launch(Required(options, "path"), options.GetValueOrDefault("args")),
            "focus" => apps.Focus(Required(options, "name")),
            "switch" => apps.Switch(Required(options, "name")),
            "quit" => ConfirmDestructive(options, "app.quit") ?? apps.Quit(Required(options, "name")),
            _ => throw new ArgumentException($"Unknown app command '{sub}'.")
        };
    }

    private object Menu(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "list" => new { menus = automation.ListMenus(TargetSpec.FromArgs(tail)) },
            "click" => automation.ClickMenu(TargetSpec.FromArgs(tail), Required(options, "text")),
            _ => throw new ArgumentException($"Unknown menu command '{sub}'.")
        };
    }

    private object Dialog(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "list" => new { dialogs = automation.ListDialogs() },
            "click" => automation.ClickDialog(TargetSpec.FromArgs(tail), Required(options, "button")),
            "input" => automation.InputDialog(TargetSpec.FromArgs(tail), Required(options, "value")),
            "dismiss" => automation.DismissDialog(TargetSpec.FromArgs(tail)),
            _ => throw new ArgumentException($"Unknown dialog command '{sub}'.")
        };
    }

    private object Clipboard(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "get" => new { text = clipboard.GetText() },
            "set" => clipboard.SetText(Required(options, "text")),
            "clear" => clipboard.Clear(),
            "paste" => input.Hotkey(new[] { "ctrl", "v" }) with { Action = "clipboard.paste" },
            _ => throw new ArgumentException($"Unknown clipboard command '{sub}'.")
        };
    }

    private object Snapshot(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "list" => new { snapshots = snapshotStore.List() },
            "show" => new { snapshot = snapshotStore.Show(Required(options, "id")) },
            "inspect" => snapshotStore.Inspect(Required(options, "id"), TargetSpec.FromArgs(tail)),
            "clean" => snapshotStore.Clean(config.SnapshotRetentionDays, config.MaxSnapshots),
            _ => throw new ArgumentException($"Unknown snapshot command '{sub}'.")
        };
    }

    private object Config(IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> options)
    {
        var sub = Sub(tail);
        return sub switch
        {
            "init" => InitConfig(),
            "show" => new { config },
            "validate" => configLoader.Validate(config),
            "get" => new { key = Required(options, "key"), value = GetConfigValue(Required(options, "key")) },
            "set" => SetConfigValue(Required(options, "key"), Required(options, "value")),
            _ => throw new ArgumentException($"Unknown config command '{sub}'.")
        };
    }

    private async Task<object> RunMcp(CancellationToken cancellationToken)
    {
        var server = new McpServer(new McpToolRegistry(), this);
        await server.RunStdioAsync(Console.In, Console.Out, cancellationToken).ConfigureAwait(false);
        return new NoOutput();
    }

    private object InitConfig()
    {
        configLoader.Save(config);
        return new { path = configLoader.ConfigPath, config };
    }

    private object SetConfigValue(string key, string value)
    {
        var updated = key switch
        {
            "snapshotDirectory" => config with { SnapshotDirectory = value },
            "snapshotRetentionDays" => config with { SnapshotRetentionDays = int.Parse(value) },
            "maxSnapshots" => config with { MaxSnapshots = int.Parse(value) },
            "safety.localOnly" => config with { Safety = config.Safety with { LocalOnly = bool.Parse(value) } },
            "safety.confirmDestructiveActions" => config with { Safety = config.Safety with { ConfirmDestructiveActions = bool.Parse(value) } },
            _ => throw new ArgumentException($"Config key '{key}' is not settable.")
        };
        var validation = configLoader.Validate(updated);
        if (!validation.Valid) throw new PbwException(new PbwError("invalid_config", string.Join("; ", validation.Errors), key));
        configLoader.Save(updated);
        return new { key, value = GetConfigValue(key, updated), path = configLoader.ConfigPath };
    }

    private object? GetConfigValue(string key, PbwConfig? source = null)
    {
        source ??= config;
        return key switch
        {
            "snapshotDirectory" => source.SnapshotDirectory,
            "snapshotRetentionDays" => source.SnapshotRetentionDays,
            "maxSnapshots" => source.MaxSnapshots,
            "safety.localOnly" => source.Safety.LocalOnly,
            "safety.confirmDestructiveActions" => source.Safety.ConfirmDestructiveActions,
            "mcp.remoteListenerEnabled" => source.Mcp.RemoteListenerEnabled,
            _ => throw new ArgumentException($"Config key '{key}' is unknown.")
        };
    }

    private static CommandResult Ok(object? data) => new(0, JsonSerializer.Serialize(PbwEnvelope<object?>.Success(data), PbwSchema.Json));
    private static string Sub(IReadOnlyList<string> tail) => tail.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? throw new ArgumentException("Subcommand is required.");
    private static string Required(IReadOnlyDictionary<string, string> options, string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"--{key} is required.");
    private static int Int(IReadOnlyDictionary<string, string> options, string key, int? fallback = null) => ArgParser.Int(options, key) ?? fallback ?? throw new ArgumentException($"--{key} is required and must be an integer.");
    private static Bounds Bounds(IReadOnlyDictionary<string, string> options) => new(Int(options, "x"), Int(options, "y"), Int(options, "width"), Int(options, "height"));
    private ActionResult? ConfirmDestructive(IReadOnlyDictionary<string, string> options, string action)
    {
        if (!config.Safety.ConfirmDestructiveActions) return null;
        if (options.TryGetValue("confirm", out var value) && bool.TryParse(value, out var confirmed) && confirmed) return null;
        return new ActionResult(action, false, "safety-policy", Message: "Destructive action requires --confirm true.");
    }
    private static IReadOnlyDictionary<string, object?> Merge(IReadOnlyDictionary<string, object?>? metadata, string key, object? value)
    {
        var copy = metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(metadata);
        copy[key] = value;
        return copy;
    }
}

internal sealed record NoOutput;

internal static class Commands
{
    public static readonly string[] All =
    {
        "see", "image", "click", "type", "press", "hotkey", "scroll", "drag", "move", "set-value",
        "perform-action", "window list", "window focus", "window move", "window resize", "window set-bounds",
        "window minimize", "window maximize", "window restore", "window close", "app list", "app launch",
        "app focus", "app switch", "app quit", "menu list", "menu click", "dialog list", "dialog click",
        "dialog input", "dialog dismiss", "clipboard get", "clipboard set", "clipboard clear", "clipboard paste",
        "snapshot list", "snapshot show", "snapshot inspect", "snapshot clean", "config init", "config show",
        "config validate", "config get", "config set", "doctor", "mcp"
    };
}
