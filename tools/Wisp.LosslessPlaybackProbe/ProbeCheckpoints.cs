using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Wisp.LosslessPlaybackProbe;

internal sealed class ProbeCheckpoints : IDisposable
{
    private readonly FileStream _stages;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _sampler;
    private readonly Func<object> _counters;
    private readonly long _started;
    private string _stage = "checkpoint-start";
    private string _call = "none";
    private int _stageLines;
    private int _sampleFailed;

    internal ProbeCheckpoints(string output, long started, Func<object> counters)
    {
        _started = started;
        _counters = counters;
        _stages = Open(Path.Combine(output, "stages.jsonl"));
        var samples = Open(Path.Combine(output, "watchdog-state.jsonl"));
        _sampler = new Thread(() => Sample(samples)) { IsBackground = true, Name = "Wisp probe scalar checkpoints" };
        _sampler.Start();
    }

    internal bool SamplerHealthy => Volatile.Read(ref _sampleFailed) == 0;

    internal void Mark(string stage)
    {
        Volatile.Write(ref _stage, stage);
        if (++_stageLines > 192) throw new CheckFailure("checkpoint-stage-limit");
        Write(_stages, "stage");
    }

    // Used for high-frequency getters: the independent sampler can identify a blocked call
    // without making every 15 ms observation perform disk I/O.
    internal void NativeCall(string call) => Volatile.Write(ref _call, call);

    private void Sample(FileStream output)
    {
        using (output)
        {
            try
            {
                // At most 81 small records. The hard watchdog never waits for this writer.
                for (var i = 0; i <= 80; i++)
                {
                    Write(output, "watchdog-sample");
                    if (_stop.Wait(250)) break;
                }
            }
            catch { Interlocked.Exchange(ref _sampleFailed, 1); }
        }
    }

    private void Write(FileStream output, string kind)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = kind,
            ElapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            Stage = Volatile.Read(ref _stage),
            NativeCall = Volatile.Read(ref _call),
            Counters = _counters()
        });
        if (bytes.Length > 2048 || output.Length + bytes.Length + 1 > 512 * 1024)
            throw new CheckFailure("checkpoint-size-limit");
        output.Write(bytes);
        output.WriteByte((byte)'\n');
        output.Flush(flushToDisk: true);
    }

    private static FileStream Open(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
        bufferSize: 4096, FileOptions.WriteThrough);

    public void Dispose()
    {
        _stop.Set();
        _stages.Dispose();
        if (_sampler.Join(500)) _stop.Dispose();
        // A blocked sampler owns its own stream. It cannot delay the watchdog or be
        // raced by this thread closing the handle underneath an in-flight write.
    }
}
