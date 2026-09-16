using System.Diagnostics;

namespace DeltaPlayer;

public interface IPlaybackClock
{
    double Now { get; }
    void WaitUntil(double deadline, CancellationToken cancel, Action check);
}

public sealed class PlaybackClock : IPlaybackClock
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    public double Now => watch.Elapsed.TotalSeconds;
    public void WaitUntil(double deadline, CancellationToken cancel, Action check)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested(); check();
            double remaining = deadline - Now;
            if (remaining <= 0) return;
            // No persistent spin loop: timing is bounded and checked again before down.
            cancel.WaitHandle.WaitOne(Math.Clamp((int)Math.Ceiling(remaining * 1000), 1, 8));
        }
    }
}

public interface IOutput
{
    bool HasHeld { get; }
    void Check();
    void Prepare(Fingering finger);
    void Down(Fingering finger);
    void Release();
    void Close();
}

public sealed class PlaybackPauseException(string message) : OperationCanceledException(message);
public sealed record PlaybackResult(bool Paused, bool Completed, int NextIndex, double Position, string? Error, string? PauseNotice = null);
public sealed record PlaybackSnapshot(double Position, int Index, string State);

public sealed class Player
{
    private CancellationTokenSource? cancellation;
    private Task<PlaybackResult>? work;
    private IOutput? residual;
    private int pauseRequested;
    private PlaybackSnapshot snapshot = new(0, 0, "idle");
    private readonly Queue<string> diagnostics = new();
    public PlaybackSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool Active => work is { IsCompleted: false };
    public bool NeedsRelease => residual != null;
    public string Diagnostics { get { lock (diagnostics) return string.Join(Environment.NewLine, diagnostics); } }

    private void Log(string text)
    {
        lock (diagnostics) { if (diagnostics.Count >= 256) diagnostics.Dequeue(); diagnostics.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {text}"); }
    }
    public Task<PlaybackResult> Start(Plan plan, Func<IOutput> create, int index = 0, double position = 0, int countdown = 0)
    {
        if (Active || NeedsRelease) throw new InvalidOperationException("上一次演奏尚未清理完成。");
        cancellation?.Dispose(); cancellation = new(); pauseRequested = 0;
        Volatile.Write(ref snapshot, new(position, index, countdown > 0 ? $"请切回游戏，{countdown} 秒后开始" : "playing"));
        var token = cancellation.Token;
        work = Task.Factory.StartNew(() => Run(plan, create, new PlaybackClock(), token, index, position, countdown), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return work;
    }
    public void Cancel(bool pause = false) { Interlocked.Exchange(ref pauseRequested, pause ? 1 : 0); cancellation?.Cancel(); }

    // Public for deterministic tests. Production calls it only on the single owner thread.
    public PlaybackResult Run(Plan plan, Func<IOutput> create, IPlaybackClock clock, CancellationToken token,
        int index = 0, double position = 0, int countdown = 0)
    {
        IOutput? output = null; string? error = null, pauseNotice = null; bool completed = false;
        double origin = 0, currentPosition = position;
        try
        {
            for (int left = countdown; left > 0; left--)
            {
                Volatile.Write(ref snapshot, new(position, index, $"请切回游戏，{left} 秒后开始"));
                clock.WaitUntil(clock.Now + 1, token, () => { });
            }
            token.ThrowIfCancellationRequested(); output = create(); output.Check();
            // Allow fresh modifier preparation, including after a pause in the middle of a note.
            double startOffset = index < plan.Notes.Length ? Math.Min(position, plan.Notes[index].Prepare) : position;
            origin = clock.Now - startOffset;
            double lastUp = double.NegativeInfinity, lastMod = double.NegativeInfinity;
            ushort lastScan = 0;
            void Check()
            {
                token.ThrowIfCancellationRequested(); output.Check();
                currentPosition = Math.Clamp(clock.Now - origin, 0, plan.Duration);
                Volatile.Write(ref snapshot, new(currentPosition, index, "playing"));
            }
            while (index < plan.Notes.Length)
            {
                var note = plan.Notes[index];
                clock.WaitUntil(origin + note.Prepare, token, Check);
                Check(); output.Prepare(note.Finger);
                if (note.Finger.Modifiers != 0) lastMod = clock.Now;
                double earliest = Math.Max(origin + note.On, Math.Max(lastMod + Music.Settle,
                    lastUp + (lastScan == note.Finger.Scan ? Music.SameKeyGap : Music.Gap)));
                clock.WaitUntil(earliest, token, Check); Check();
                double target = origin + .1 + note.Source.Start / plan.Speed;
                double targetEnd = origin + .1 + note.Source.End / plan.Speed;
                if (clock.Now - target > Music.MaxLate || clock.Now + Music.MinHold > targetEnd + Music.MaxLate)
                    throw new InvalidOperationException("播放出现明显延迟，已停止。请关闭占用较高的后台程序后重试。");
                output.Down(note.Finger); double actualDown = clock.Now;
                // Mark a started note consumed before cancellation, so resume never replays it.
                index++;
                double off = Math.Max(origin + note.Off, actualDown + Music.MinHold);
                clock.WaitUntil(off, token, Check);
                output.Release(); lastUp = clock.Now; lastScan = note.Finger.Scan;
                if (note.Finger.Modifiers != 0) lastMod = clock.Now;
                Log($"note={note.Source.Id} latenessMs={(actualDown - target) * 1000:F1} holdMs={(lastUp - actualDown) * 1000:F1}");
                if (lastUp > targetEnd + Music.MaxLate) throw new InvalidOperationException("播放出现明显卡顿，已停止并释放按键。");
            }
            clock.WaitUntil(origin + plan.Duration, token, Check);
            currentPosition = plan.Duration; completed = true;
        }
        catch (PlaybackPauseException e) { Interlocked.Exchange(ref pauseRequested, 1); pauseNotice = e.Message; Log(e.Message); }
        catch (OperationCanceledException) { }
        catch (Exception e) { error = e.Message; Log(e.ToString()); }
        finally
        {
            if (output != null)
            {
                Exception? releaseError = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { output.Release(); output.Close(); releaseError = null; break; }
                    catch (Exception e) { releaseError = e; }
                }
                if (releaseError != null || output.HasHeld)
                {
                    residual = output;
                    error = "按键或试听设备未能清理，请点击重试释放。" + releaseError?.Message;
                    Log(error);
                }
            }
        }
        bool paused = Volatile.Read(ref pauseRequested) == 1 && error == null && !completed;
        Log($"session end: completed={completed}, paused={paused}, next={index}, error={error ?? "none"}");
        return new(paused, completed, paused ? index : 0, paused || completed ? currentPosition : 0, error, pauseNotice);
    }
    public async Task RetryRelease()
    {
        if (Active) throw new InvalidOperationException("请先停止演奏。");
        var output = residual;
        if (output == null) return;
        await Task.Run(() => { output.Release(); output.Close(); if (output.HasHeld) throw new InvalidOperationException("仍有按键未释放。"); });
        residual = null;
    }
}
