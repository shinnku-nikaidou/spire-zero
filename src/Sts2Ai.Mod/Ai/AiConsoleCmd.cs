using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// The game's console (the ~ key) gets one command, <c>ai</c>, whose first word is what to do: the toolbar and settings
/// without the mouse, the brain's answer read whole, questions on the side, the run's review, and jobs that run the
/// shipped simulator beside the game. The game finds it in the mod's assembly by itself. It changes nothing in the
/// game a player could not, so it is no debug command: the game does not mark the run for it. The spoiler (scout) is
/// the one exception, and marks the run as the game's own debug commands do (<see cref="ScoutConsoleCmd"/>).
/// </summary>
public sealed class AiConsoleCmd : AbstractConsoleCmd
{
    private sealed record Sub(string Name, string Args, string Help, Func<string[], string> Run, Func<string[], IEnumerable<string>>? Complete = null);

    private static readonly string[] Scopes = ["fight", "act", "run"];

    private static readonly Sub[] Subs =
    [
        new("help", "[command]", "this list, or one command's", a => Help(a.ElementAtOrDefault(0))),
        new("status", "", "the brain, the run, the settings, what is open", _ => Out.Code(A.Status())),
        new("version", "", "the mod's, the game's and the protocol's versions", _ => Version()),
        new("set", "[name] [value]", "show or change a setting (as the settings screen and the panel do)", Set, SetCompletions),
        new("auto", "[on|off|until boss|elite|act|floor N|end]", "auto-play; until a stop of your own instead of the settings'",
            a => a.ElementAtOrDefault(0) switch
            {
                null => A.Drive(Command.Auto),
                "on" => A.Drive(Command.Auto, true),
                "off" => A.Drive(Command.Auto, false),
                "until" => A.Auto(a.ElementAtOrDefault(1) ?? "", a.ElementAtOrDefault(2)),
                var x => throw new AiError($"auto {x}?"),
            }, a => a.Length <= 1 ? ["on", "off", "until"] : a[0] == "until" && a.Length == 2 ? ["boss", "elite", "act", "floor", "end"] : []),
        new("step", "", "the AI plays the next action", _ => A.Drive(Command.Step)),
        new("finish", "", "the AI plays to the end of this turn, or of this room", _ => A.Drive(Command.Finish)),
        new("why", "[N]", "every option the brain scored here; N: that option's plan", a => A.Why(a.Length > 0 ? Int(a[0]) : null)),
        new("legal", "", "the actions this decision allows, numbered for try", _ => A.Legal()),
        new("try", "<N|action>", "your option against the AI's: what it costs, and its plan", a => A.Try(a.Length > 0 ? string.Join(' ', a) : throw new AiError("try N: ai legal numbers them"))),
        new("line", "", "the fight as the picked line plays it, turn by turn", _ => A.Line()),
        new("lethal", "", "whether the plan kills this turn", _ => A.Lethal()),
        new("danger", "", "what the enemies intend, and what the plan takes", _ => A.Danger()),
        new("deep", "", "this decision asked of SL's max (the solver), beside the game", _ => A.Deep()),
        new("compare", "", "this decision asked of the other environment (SL / NoSL)", _ => A.Compare()),
        new("replay", "[decision]", "a recent decision asked again at today's settings", a => A.Replay(a.Length > 0 ? Int(a[0]) : null)),
        new("deck", "", "what each card, relic and potion is worth to V", _ => A.Deck()),
        new("worth", "card|relic|potion <id> [+]", "what one more would add to V", a => a.Length >= 2 ? A.Worth(a[0], a[1], a.ElementAtOrDefault(2) == "+") : throw new AiError("worth card|relic|potion ID"),
            WorthCompletions),
        new("review", "[fight|act|run]", "your own answers against the AI's, worst first", a => A.Review(a.ElementAtOrDefault(0) ?? "run"), a => a.Length <= 1 ? Scopes : []),
        new("graph", "", "V's chance to win along the floors", _ => A.Graph()),
        new("quiz", "on|off|stats|reset", "the panel hides the answer until you chose; your scores by kind of decision", a => A.Quiz(a.ElementAtOrDefault(0) ?? "stats"),
            a => a.Length <= 1 ? ["on", "off", "stats", "reset"] : []),
        new("parity", "", "SL's engine line against the real fight at each turn end", _ => A.Parity()),
        new("mark", "[note]", "write this decision down whole, with your note, to give the brain again", a => A.Mark(string.Join(' ', a))),
        new("race", "[low|medium|high]", "the AI plays this run's seed from floor 1, beside the game", a => Jobs.Race(a.ElementAtOrDefault(0) ?? "low"), a => a.Length <= 1 ? ["low", "medium", "high"] : []),
        new("rollout", "[N] [low|medium|high]", "the AI plays the rest of this run N times from the room's save, chance drawn anew", a => Jobs.Rollout(a.Length > 0 ? Int(a[0]) : 8, a.ElementAtOrDefault(1) ?? "low")),
        new("jobs", "", "the questions and jobs open", _ => Jobs.List()),
        new("stop", "", "answer the open questions now, end the jobs", _ => $"{A.Hurry()} questions hurried; {Jobs.StopAll()} jobs ended"),
        new("restart", "", "start the brain again (the game goes on)", _ => BrainLink.Restart()),
        new("mp", "[on|off]", "advice in runs of more than one player, NoSL, for this game session (off at start)", Mp, a => a.Length <= 1 ? ["on", "off"] : []),
    ];

    private static Assist A => Assist.Current ?? throw new AiError("the mod is off (its panel says why)");

    public override string CmdName => "ai";

    public override string Args => "<command> [args]: ai help";

    public override string Description => "Spire Zero, the AI: its answers, questions, review, auto-play (ai help).";

    public override bool IsNetworked => false;

    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string name = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var sub = Subs.FirstOrDefault(s => s.Name == name);
        if (sub == null) return new CmdResult(false, $"no ai command {name}: ai help lists them");
        try
        {
            return new CmdResult(true, sub.Run(args[1..]));
        }
        catch (AiError e)
        {
            return new CmdResult(false, Out.Esc(e.Message));
        }
        catch (Exception e)
        {
            ModLog.Warn("ai", $"ai {string.Join(' ', args)}: {e}");
            return new CmdResult(false, Out.Esc($"{e.GetType().Name}: {e.Message}"));
        }
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1) return CompleteArgument(Subs.Select(s => s.Name), [], args.ElementAtOrDefault(0) ?? "", CompletionType.Subcommand);
        var sub = Subs.FirstOrDefault(s => s.Name == args[0].ToLowerInvariant());
        if (sub?.Complete == null) return base.GetArgumentCompletions(player, args);
        var rest = args[1..];
        return CompleteArgument(sub.Complete(rest), args[..^1], args[^1], CompletionType.Argument,
            (candidate, partial) => candidate.Contains(partial, StringComparison.OrdinalIgnoreCase));
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : throw new AiError($"{text} is no number");

    private static string Help(string? name)
    {
        if (name != null)
        {
            var sub = Subs.FirstOrDefault(s => s.Name == name) ?? throw new AiError($"no ai command {name}");
            return Out.Code($"ai {sub.Name} {sub.Args}\n  {sub.Help}");
        }
        int width = Subs.Max(s => (s.Name + " " + s.Args).Length);
        return Out.Code(Subs.Select(s => $"ai {Out.Pad(s.Name + " " + s.Args, width)}  {s.Help}")
            .Append("ai scout [route]  (its own command) what the rooms ahead hold, from the game's save: a spoiler, the game marks the run"));
    }

    private static string Mp(string[] args)
    {
        switch (args.ElementAtOrDefault(0))
        {
            case null:
                return $"mp {(Assist.Multiplayer ? "on" : "off")}";
            case "on":
                Assist.Multiplayer = true;
                return "mp on: in a run of more than one player the AI advises you, in NoSL, until the game closes (teammates are taken to pass in its fights)";
            case "off":
                Assist.Multiplayer = false;
                return "mp off";
            default:
                throw new AiError("mp on|off");
        }
    }

    private static string Version()
    {
        string modDir = Path.GetDirectoryName(typeof(AiConsoleCmd).Assembly.Location)!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "Sts2Ai.json")));
        return Out.Code([
            $"mod      {manifest.RootElement.GetProperty("version").GetString()}",
            $"game     {MegaCrit.Sts2.Core.Nodes.NGame.GetGameVersion()}",
            $"protocol {WireMessage.Version}",
            $"brain    {BrainLink.Current?.Where ?? "none"}",
        ]);
    }

    // ---- settings ----

    /// <summary>The settings the console may change: every one the settings screen and the panel have.</summary>
    private static IEnumerable<PropertyInfo> Settable => typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod?.IsPublic == true);

    private static string Set(string[] args)
    {
        var settings = Settings.Current;
        if (args.Length == 0)
            return Out.Code(Settable.Select(p => $"{Out.Pad(p.Name.ToLowerInvariant(), 14)} {Shown(p.GetValue(settings))}   {Choices(p.PropertyType)}"));
        var property = Settable.FirstOrDefault(p => p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase)) ?? throw new AiError($"no setting {args[0]}");
        if (args.Length == 1) return Out.Esc($"{property.Name.ToLowerInvariant()} {Shown(property.GetValue(settings))}");
        object value = Parse(property.PropertyType, args[1]);
        // A record's copy, the property set on it as `with` would.
        var copy = (Settings)typeof(Settings).GetMethod("<Clone>$")!.Invoke(settings, null)!;
        property.SetValue(copy, value);
        Settings.Set(copy);
        return Out.Esc($"{property.Name.ToLowerInvariant()} {Shown(value)}");
    }

    private static string Shown(object? value) => value switch
    {
        bool b => b ? "on" : "off",
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        null => "—",
        _ => value.ToString()!.ToLowerInvariant(),
    };

    private static string Choices(Type type) =>
        type == typeof(bool) ? "on|off" : type.IsEnum ? string.Join("|", Enum.GetNames(type).Select(n => n.ToLowerInvariant())) : type == typeof(float) ? "a number" : "";

    private static object Parse(Type type, string text)
    {
        if (type == typeof(bool))
            return text.ToLowerInvariant() switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => throw new AiError($"{text}: on or off") };
        if (type.IsEnum)
            return Enum.TryParse(type, text, ignoreCase: true, out var e) ? e! : throw new AiError($"{text}: {Choices(type)}");
        if (type == typeof(float))
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : throw new AiError($"{text} is no number");
        throw new AiError($"the console cannot set a {type.Name}");
    }

    private static IEnumerable<string> SetCompletions(string[] args)
    {
        if (args.Length <= 1) return Settable.Select(p => p.Name.ToLowerInvariant());
        var property = Settable.FirstOrDefault(p => p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        return property == null ? [] : Choices(property.PropertyType).Split('|', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> WorthCompletions(string[] args) => args.Length switch
    {
        <= 1 => ["card", "relic", "potion"],
        2 => args[0] switch
        {
            "card" => ModelDb.AllCards.Select(c => c.Id.Entry),
            "relic" => ModelDb.AllRelics.Select(r => r.Id.Entry),
            "potion" => ModelDb.AllPotions.Select(p => p.Id.Entry),
            _ => [],
        },
        _ => ["+"],
    };
}
