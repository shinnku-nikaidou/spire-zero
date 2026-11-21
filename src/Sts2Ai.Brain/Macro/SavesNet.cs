using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>A run and f of each potion it holds as the trainer gave it, in the belt's order, which loading checks, in
/// its environment (null: SL).</summary>
public sealed record SavesTest(RunView Run, IReadOnlyList<double> Saves, string? Env = null);

/// <summary>f as <c>learn/f/export.py</c> writes it: V's format (<see cref="NetFile"/>) with f's graph, whose inputs are
/// V's and the potions asked about, and whose output is each one's f.</summary>
public sealed record SavesFile(
    string Name, IReadOnlyDictionary<string, IReadOnlyList<string>> Vocab, string Onnx, NetState State, IReadOnlyList<SavesTest> Tests);

/// <summary>
/// f (plans/2026-11-21-1655-fight-score-f.md): of a run, each potion it holds with the HP it will save in the fight
/// where it is drunk, never below 0, which a fight's score counts (<see cref="CombatNet.FightValue"/>). It reads
/// the run as V does, all but the HP and max HP, so a run's f is the same at any HP.
/// </summary>
public sealed class SavesNet : IDisposable
{
    private readonly SavesFile _file;
    private readonly RunGraph _graph;

    private SavesNet(SavesFile file)
    {
        _file = file;
        _graph = new RunGraph(file.Vocab, file.State, file.Onnx);
    }

    public string Name => _file.Name;

    /// <summary>The net of a file on disk.</summary>
    public static SavesNet Read(string path) => Load(ProtocolJson.Deserialize<SavesFile>(File.ReadAllText(path)));

    /// <summary>The net of a file, once it gives every test run's potions the trainer's f.</summary>
    public static SavesNet Load(SavesFile file)
    {
        var net = new SavesNet(file);
        foreach (var test in file.Tests)
        {
            var saves = net.Saves(test.Run, test.Env is { } env ? Enum.Parse<PlayEnvironment>(env) : PlayEnvironment.SL);
            if (saves.Count != test.Saves.Count || saves.Zip(test.Saves).Any(p => Math.Abs(p.First - p.Second) > Math.Max(1e-3 * Math.Abs(p.Second), 1e-3)))
            {
                net.Dispose();
                throw new InvalidDataException($"net {file.Name} does not give a test run's potions the HP the trainer said they save");
            }
        }
        return net;
    }

    /// <summary>Each potion <paramref name="run"/> holds, in the belt's order, with f in <paramref name="env"/>.</summary>
    public IReadOnlyList<double> Saves(RunView run, PlayEnvironment env)
    {
        var (output, tokens) = _graph.Run(run, env);
        return [.. output.Take(tokens.Potions.Count).Select(f => (double)f)];
    }

    public void Dispose() => _graph.Dispose();
}
