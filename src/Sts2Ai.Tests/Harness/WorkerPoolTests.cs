using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

public class WorkerPoolTests
{
    private static readonly WorkerRequest Request = new SlPromptRequest(0, new NodeClone("", []), []);

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

    [Fact]
    public void AWorkerThatRepliesWithAnErrorIsReplaced()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"failed-{Guid.NewGuid():N}");
        try
        {
            // The first process replies with an error and exits, as a worker does after a request that threw; the one
            // started in its place answers.
            using var pool = Pool($"if [ -e {marker} ]; then echo '{{\"id\":0,\"lines\":[],\"game_errors\":[]}}'; read rest; " +
                                  $"else touch {marker}; echo '{{\"id\":0,\"error\":\"boom\",\"game_errors\":[]}}'; fi");
            var e = Assert.Throws<InvalidOperationException>(() => pool.Map([Request]));
            Assert.Contains("boom", e.Message);
            // Every worker in the pool is alive: its processor time reads.
            _ = pool.CpuTime();
            Assert.Equal(0, Assert.Single(pool.Map([Request])).Id);
            Assert.Equal(1, pool.Restarts);
        }
        finally
        {
            File.Delete(marker);
        }
    }
}
