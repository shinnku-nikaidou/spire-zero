using System.CommandLine;
using Sts2Ai.Brain;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>encode: the nets' inputs for a trainer (docs: learning/reference §2), asked on stdin and answered on stdout
/// (<see cref="TokenServer"/>). It needs nothing of the game: <c>src/Sts2Ai.Encode</c> is the same without it.</summary>
internal static class EncodeCommand
{
    public static Command Command()
    {
        var command = Opt.Command("encode", "the nets' inputs of the decisions asked on stdin, for a trainer (docs: learning/reference §2)");
        command.SetAction(_ =>
        {
            TokenServer.Serve(Console.In, Console.OpenStandardOutput(), Program.Commit);
            return 0;
        });
        return command;
    }
}
