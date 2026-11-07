using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// Simulator worker processes (docs: simulator/pool §2), each started as <c>COMMAND serve OPTIONS</c> and spoken to in JSON
/// lines over its stdin and stdout (<see cref="WorkerHello"/>, <see cref="WorkerReply"/>). A worker serves one request
/// at a time; <see cref="Map"/> hands each request to whichever worker is free and returns the replies in request
/// order, whatever order they finish in, so timing cannot change a result. A worker that dies, or does not answer
/// within the timeout (a worker alive but stuck), is killed and started again and gets its request once more; a
/// second failure fails the call. A reply with an error fails it too, and the game's Log.Error lines in the replies
/// are kept for <see cref="TakeGameErrors"/>.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>How long a worker may take to boot or to answer one request. A request is one world's search, a few
    /// seconds at most; this is for a worker that is stuck, not slow.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ProcessStartInfo _start;
    private readonly TimeSpan _timeout;
    private readonly BlockingCollection<Worker> _free = [];
    private int _restarts;
    private readonly List<string> _gameErrors = [];

    /// <summary>Starts <paramref name="count"/> workers in parallel and waits until each has booted.</summary>
    /// <param name="command">The worker program and its leading arguments, e.g. <c>dotnet Sts2Ai.SimWorker.dll</c>.</param>
    /// <param name="options">The options of <c>serve</c>, such as the combat net the workers search with.</param>
    /// <param name="timeout">How long a worker may take to boot or answer (<see cref="DefaultTimeout"/>).</param>
    public WorkerPool(IReadOnlyList<string> command, int count, IReadOnlyList<string>? options = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _start = new ProcessStartInfo(command[0], [.. command.Skip(1), "serve", .. options ?? []])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        // The environment is filled in on first read, which is not thread-safe: read it once before the parallel starts.
        _ = _start.Environment;
        var workers = Enumerable.Range(0, count).AsParallel().Select(_ => new Worker(_start, _timeout)).ToList();
        Game = workers[0].Game;
        foreach (var w in workers) _free.Add(w);
        Count = count;
    }

    public int Count { get; }

    /// <summary>The game version the workers run.</summary>
    public string Game { get; }

    /// <summary>Workers started again after dying.</summary>
    public int Restarts => _restarts;

    /// <summary>The game's Log.Error lines in the workers' replies since the last call.</summary>
    public List<string> TakeGameErrors()
    {
        var errors = _gameErrors.ToList();
        _gameErrors.Clear();
        return errors;
    }

    /// <exception cref="InvalidOperationException">A reply has an error (an exception in its worker).</exception>
    public IReadOnlyList<WorkerReply> Map(IReadOnlyList<WorkerRequest> requests)
    {
        var replies = new WorkerReply[requests.Count];
        Parallel.For(0, requests.Count, new ParallelOptions { MaxDegreeOfParallelism = Count }, i =>
        {
            var line = ProtocolJson.Serialize(requests[i]);
            var worker = _free.Take();
            try
            {
                replies[i] = Send(ref worker, line, requests[i].Id);
            }
            finally
            {
                _free.Add(worker);
            }
        });
        foreach (var r in replies) _gameErrors.AddRange(r.GameErrors);
        if (replies.FirstOrDefault(r => r.Error != null) is { } failed) throw new InvalidOperationException($"request {failed.Id}: {failed.Error}");
        return replies;
    }

    private WorkerReply Send(ref Worker worker, string line, long id)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var text = worker.Call(line);
                WorkerReply reply;
                try
                {
                    reply = ProtocolJson.Deserialize<WorkerReply>(text);
                }
                catch (System.Text.Json.JsonException e)
                {
                    throw new InvalidOperationException($"worker sent a bad reply ({e.Message}): {(text.Length > 300 ? text[..300] + "..." : text)}");
                }
                if (reply.Id != id) throw new InvalidOperationException($"worker replied to request {reply.Id}, expected {id}");
                return reply;
            }
            catch (WorkerDiedException) when (attempt == 0)
            {
                Interlocked.Increment(ref _restarts);
                worker.Dispose();
                worker = new Worker(_start, _timeout);
            }
        }
    }

    /// <summary>Call when no <see cref="Map"/> is running: every worker is then free.</summary>
    public void Dispose()
    {
        _free.CompleteAdding();
        foreach (var w in _free) w.Dispose();
    }

    private sealed class WorkerDiedException(string message) : Exception(message);

    private sealed class Worker : IDisposable
    {
        private const int StderrLines = 20;

        private readonly Process _process;
        private readonly TimeSpan _timeout;
        /// <summary>The worker's last stderr lines, for the report if it dies.</summary>
        private readonly Queue<string> _stderr = new();

        /// <summary>Starts the process and waits for its hello.</summary>
        public Worker(ProcessStartInfo start, TimeSpan timeout)
        {
            _timeout = timeout;
            _process = new Process { StartInfo = start };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    if (_stderr.Count > StderrLines) _stderr.Dequeue();
                }
            };
            _process.Start();
            _process.BeginErrorReadLine();
            Game = ProtocolJson.Deserialize<WorkerHello>(ReadLine("boot")).Game;
        }

        public string Game { get; }

        public string Call(string line)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                return ReadLine("answer");
            }
            catch (IOException)
            {
                throw Died();
            }
        }

        /// <summary>The worker's next line; a worker that gives none within the timeout is killed.</summary>
        private string ReadLine(string what)
        {
            var read = _process.StandardOutput.ReadLineAsync();
            bool done;
            try
            {
                done = read.Wait(_timeout);
            }
            catch (AggregateException e) when (e.InnerException is IOException)
            {
                throw Died();
            }
            if (!done)
            {
                _process.Kill();
                _process.WaitForExit(1000);
                throw new WorkerDiedException($"worker {_process.Id} did not {what} within {_timeout.TotalSeconds:F0} s, killed:\n{Tail()}");
            }
            return read.Result ?? throw Died();
        }

        private WorkerDiedException Died()
        {
            _process.WaitForExit(1000);
            string code = _process.HasExited ? $"exit code {_process.ExitCode}" : "still running";
            return new WorkerDiedException($"worker {_process.Id} died ({code}):\n{Tail()}");
        }

        private string Tail()
        {
            lock (_stderr) return string.Join("\n", _stderr);
        }

        /// <summary>Closing stdin ends the worker's loop; one that does not exit soon is killed.</summary>
        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5000)) _process.Kill();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
            }
            _process.Dispose();
        }
    }
}
