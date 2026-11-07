using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

public class WorkerPoolTests
{
    private static readonly WorkerRequest Request = new SlTurnRequest(0, new NodeClone("", []), [], 1, 1, null, []);

    /// <summary>A worker as a shell script: it says hello, then runs <paramref name="script"/> on its first request.</summary>
    private static WorkerPool Pool(string script) =>
        new(["/bin/sh", "-c", $"echo '{{\"game\":\"test\"}}'; read request; {script}"], 1, timeout: TimeSpan.FromSeconds(1));

    [Fact]
    public void AWorkerStuckTwiceFailsTheCall()
    {
        using var pool = Pool("sleep 30");
        var e = Assert.ThrowsAny<Exception>(() => pool.Map([Request]));
        Assert.Contains("did not answer within 1 s", e.InnerException?.Message ?? e.Message);
        Assert.Equal(1, pool.Restarts);
    }

    [Fact]
    public void AWorkerStuckOnceIsRestartedAndAnswers()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"stuck-{Guid.NewGuid():N}");
        try
        {
            // The first process leaves the marker and hangs; the one started in its place answers.
            using var pool = Pool($"if [ -e {marker} ]; then echo '{{\"id\":0,\"lines\":[],\"game_errors\":[]}}'; read rest; else touch {marker}; sleep 30; fi");
            var reply = Assert.Single(pool.Map([Request]));
            Assert.Equal(0, reply.Id);
            Assert.Equal(1, pool.Restarts);
        }
        finally
        {
            File.Delete(marker);
        }
    }
}
