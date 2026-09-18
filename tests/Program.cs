using DeltaPlayer;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using System.Diagnostics;

var cases = new List<(string, Action)>();
void Test(string name, Action run) => cases.Add((name, run));
void Assert(bool ok, string message = "assertion failed") { if (!ok) throw new Exception(message); }
void Near(double actual, double expected, double epsilon = .000001) => Assert(Math.Abs(actual - expected) <= epsilon, $"expected {expected}, actual {actual}");
void Reject(Action run) { try { run(); } catch (Exception) { return; } throw new Exception("expected rejection"); }
Tone N(int id, double start, double end, int pitch = 60) => new(id, start, end, pitch, 90, "test");
Plan P(params Tone[] tones) => new(Music.Compile(tones, 0, 1) ?? throw new Exception("plan rejected"), tones[^1].End + .1, 1, 0, 0, "test");
Song S(params Tone[] tones) => new("test", "", [new("0", "声部 1", tones)], tones[^1].End, []);
byte[] Midi(params TrackChunk[] tracks)
{
    using var stream = new MemoryStream();
    new MidiFile(tracks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }.Write(stream);
    return stream.ToArray();
}
Test("TXT rhythm and whitespace", () => {
    var a = ScoreImport.ParseText("BPM=120\n1 2_ 3_. 5- | 0 【1】 (5)");
    Near(a.Voices[0].Notes[0].End, .5); Near(a.Voices[0].Notes[1].End, .75);
    Assert(a.Voices[0].Notes[^2].Pitch == 72); Assert(a.Voices[0].Notes[^1].Pitch == 55);
    var b = ScoreImport.ParseText("1\n\n 2\t3"); Near(b.Duration, 1.5);
});
Test("TXT triplets", () => {
    var song = ScoreImport.ParseText("BPM=60\nT{1_ 2_ 【3_】} 4");
    var notes = song.Voices[0].Notes;
    Near(notes[0].Start, 0); Near(notes[0].End, 1.0 / 3.0);
    Near(notes[1].Start, 1.0 / 3.0); Near(notes[1].End, 2.0 / 3.0);
    Near(notes[2].Start, 2.0 / 3.0); Near(notes[2].End, 1);
    Near(song.Duration, 2);
    Reject(() => ScoreImport.ParseText("T{1 2}"));
    Reject(() => ScoreImport.ParseText("T{1 2 3 4}"));
    Reject(() => ScoreImport.ParseText("T{1 T{2 3 4} 5}"));
    Reject(() => ScoreImport.ParseText("T{1 2 3"));
});
Test("TXT standalone Chinese duration dash", () => {
    var song = ScoreImport.ParseText("1 — — | 0 — 2");
    Near(song.Voices[0].Notes[0].Start, 0); Near(song.Voices[0].Notes[0].End, 1.5);
    Near(song.Voices[0].Notes[1].Start, 2.5); Near(song.Duration, 3);
    Reject(() => ScoreImport.ParseText("— 1"));
});
Test("project score files accept Chinese duration dashes", () => {
    string? root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "简谱", "春日影.txt"))) root = Directory.GetParent(root)?.FullName;
    Assert(root != null, "project score directory not found");
    foreach (string name in new[] { "春日影.txt", "反乌托邦 x 拼凑.txt" })
    {
        var song = ScoreImport.Load(Path.Combine(root!, "简谱", name));
        Assert(song.Voices[0].Notes.Length > 0, name + " has no notes");
    }
});
Test("TXT strict malformed inputs", () => {
    foreach (string text in new[] { "#0", "(0)", "【1)", "【1", "((1))", "1___", "1..", "1歌词", "#", "BPM=0\n1", "BPM=100x 1", "1" + new string('-', 65), "0" }) Reject(() => ScoreImport.ParseText(text));
});
Test("TXT line-column errors", () => { try { ScoreImport.ParseText("1\n2 ?"); throw new Exception(); } catch (FormatException e) { Assert(e.Message.Contains("第 2 行、第 3 列")); } });
Test("TXT limits and cancellation", () => { Reject(() => ScoreImport.ParseText(string.Concat(Enumerable.Repeat("1_ ", 30001)))); var c = new CancellationToken(true); Reject(() => ScoreImport.ParseText("1 2", cancel: c)); });
Test("MIDI defaults", () => {
    var data = Midi(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 }));
    var s = ScoreImport.ReadMidi(data); Near(s.Duration, .5); Assert(s.Voices.Length == 1); Near(s.Voices[0].Notes[0].End, .5);
});
Test("MIDI global tempo crossing long note", () => {
    var s = ScoreImport.ReadMidi(Midi(new TrackChunk(new SetTempoEvent(500000), new SetTempoEvent(1000000) { DeltaTime = 480 }),
        new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 960 })));
    Near(s.Voices[0].Notes[0].End, 1.5);
});
Test("MIDI repeated notes FIFO and velocity zero", () => {
    var s = ScoreImport.ReadMidi(Midi(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90),
        new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80) { DeltaTime = 120 },
        new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 120 },
        new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 240 })));
    Assert(s.Voices[0].Notes.Length == 2); Near(s.Voices[0].Notes[0].End, .25); Near(s.Voices[0].Notes[1].Start, .125);
});
Test("MIDI drums excluded", () => {
    var s = ScoreImport.ReadMidi(Midi(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 }),
        new TrackChunk(new NoteOnEvent((SevenBitNumber)36, (SevenBitNumber)90) { Channel = (FourBitNumber)9 }, new NoteOffEvent((SevenBitNumber)36, (SevenBitNumber)0) { Channel = (FourBitNumber)9, DeltaTime = 480 })));
    Assert(s.Voices.Length == 1);
});
Test("MIDI unmatched note warning", () => {
    var s = ScoreImport.ReadMidi(Midi(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 }, new NoteOnEvent((SevenBitNumber)61, (SevenBitNumber)90))));
    Assert(s.Warnings.Length == 1); Assert(s.Voices[0].Notes.Length == 1);
});
Test("MIDI truncated and invalid division", () => {
    var bytes = Midi(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 }));
    Reject(() => ScoreImport.ReadMidi(bytes[..^5])); bytes[12] = 0; bytes[13] = 0; Reject(() => ScoreImport.ReadMidi(bytes)); Reject(() => ScoreImport.ReadMidi([0, 1, 2]));
});
Test("single voice preserves new attacks without tails", () => {
    var notes = Music.Monophonic([N(1, 0, 4, 48), N(2, 0, 1, 72), N(3, .5, 1, 60), N(4, 1, 2, 60)]);
    Assert(notes.Length == 3); Assert(notes[0].Id == 2); Near(notes[0].End, .5); Assert(notes[^1].Id == 4);
});
Test("mapping range and sharp", () => {
    Assert(Music.Map(48)?.Modifiers == 1); Assert(Music.Map(61)?.Modifiers == 4); Assert(Music.Map(85)?.Scan == 0x33);
    Assert(Music.Map(47) == null && Music.Map(86) == null);
});
Test("automatic whole octave preserves intervals", () => {
    var result = Music.Adapt(S(N(1, 0, .5, 96), N(2, .5, 1, 98)), 0);
    Assert(result.Plan != null); Assert(result.Plan!.Octaves < 0);
    Assert(result.Plan.Notes[1].Finger.Pitch - result.Plan.Notes[0].Finger.Pitch == 2);
});
Test("out-of-range needs explicit partial choice", () => {
    var song = S(N(1, 0, .5, 20), N(2, .5, 1, 100)); var blocked = Music.Adapt(song, 0);
    Assert(blocked.Plan == null && blocked.Missing > 0);
    var partial = Music.Adapt(song, 0, allowPartial: true); Assert(partial.Plan != null && partial.Plan.Missing > 0);
});
Test("dense notes fail instead of deleting or drifting", () => {
    var song = S(Enumerable.Range(0, 100).Select(i => N(i, i * .025, (i + 1) * .025)).ToArray());
    Assert(Music.Adapt(song, 0).Plan == null);
});
Test("physical constraints at different speeds", () => {
    foreach (double speed in new[] { .5, 1.0, 1.2 }) {
        var notes = Music.Compile([N(1, 0, .5, 49), N(2, .5, 1, 73), N(3, 1, 1.5, 73)], 0, speed)!;
        Assert(notes != null);
        foreach (var note in notes!) { Assert(note.On - note.Prepare >= Music.Settle - 1e-9); Assert(note.Off - note.On >= Music.MinHold - 1e-9); }
        Assert(notes[2].On - notes[1].Off >= Music.SameKeyGap - 1e-9);
    }
});
Test("runtime success with fake clock", () => {
    var clock = new FakeClock(); var output = new Recording(clock); var player = new Player();
    var result = player.Run(P(N(1, 0, .5, 61), N(2, .5, 1, 61)), () => output, clock, default);
    Assert(result.Completed && result.Error == null); Assert(!output.HasHeld); Assert(output.Events.Count(x => x.Kind == "down") == 2);
    var prepared = output.Events.First(e => e.Kind == "prepare").Time; var down = output.Events.First(e => e.Kind == "down").Time; Assert(down - prepared >= Music.Settle - 1e-9);
});
Test("actual modifier completion anchors settle", () => {
    var clock = new FakeClock(); var output = new Recording(clock) { PreparationCost = .008 };
    var result = new Player().Run(P(N(1, 0, .5, 61)), () => output, clock, default);
    Assert(result.Completed); double prep = output.Events.First(e => e.Kind == "prepare").Time, down = output.Events.First(e => e.Kind == "down").Time;
    Assert(down - prep >= Music.Settle - 1e-9);
});
Test("late wake never bursts overdue notes", () => {
    var clock = new FakeClock { LateAt = .1, Lateness = .2 }; var output = new Recording(clock);
    var result = new Player().Run(P(N(1, 0, .5)), () => output, clock, default);
    Assert(result.Error != null); Assert(!output.Events.Any(e => e.Kind == "down")); Assert(!output.HasHeld);
});
Test("focus loss after preparation releases modifiers", () => {
    var clock = new FakeClock(); var output = new Recording(clock) { LoseFocusAt = .085 };
    var result = new Player().Run(P(N(1, 0, .5, 61)), () => output, clock, default);
    Assert(result.Error != null && !output.HasHeld); Assert(!output.Events.Any(e => e.Kind == "down"));
});
Test("cancel held long note clears input", () => {
    var cts = new CancellationTokenSource(); var clock = new FakeClock { CancelAt = .2, Cancel = cts }; var output = new Recording(clock);
    var result = new Player().Run(P(N(1, 0, 5, 61)), () => output, clock, cts.Token);
    Assert(!result.Completed && result.Error == null); Assert(!output.HasHeld); Assert(output.Events.Count(e => e.Kind == "down") == 1);
});
Test("uncertain native send and independent releases", () => {
    var sent = new List<string>(); bool fail = true;
    var output = new GameOutput(() => { }, (code, mouse, down) => { sent.Add($"{code}/{mouse}/{down}"); if (down && !mouse) throw new Exception("uncertain down"); if (!down && mouse && fail) throw new Exception("up failed"); });
    output.Prepare(new(49, 0x2c, 5)); Reject(() => output.Down(new(49, 0x2c, 5))); Reject(output.Release);
    Assert(output.HasHeld); Assert(sent.Contains("44/False/False")); Assert(sent.Contains("1/True/False") && sent.Contains("4/True/False"));
    fail = false; output.Release(); Assert(!output.HasHeld); int count = sent.Count; output.Release(); Assert(sent.Count == count);
});
Test("cleanup failure blocks restarting", () => {
    var clock = new FakeClock(); var output = new Recording(clock) { ReleaseFails = true }; var player = new Player();
    var result = player.Run(P(N(1, 0, .5)), () => output, clock, default);
    Assert(result.Error != null && player.NeedsRelease);
    Reject(() => player.Start(P(N(2, 0, .5)), () => new Recording(clock)));
    output.ReleaseFails = false; player.RetryRelease().GetAwaiter().GetResult(); Assert(!player.NeedsRelease);
});
Test("stop-start lifecycle uses one owner", () => {
    var clock = new FakeClock(); var output = new Recording(clock); var player = new Player();
    var task = player.Start(P(N(1, 0, 5)), () => output, countdown: 2);
    Reject(() => player.Start(P(N(2, 0, 1)), () => output)); player.Cancel();
    try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
    Assert(!player.Active && !player.NeedsRelease);
});
Test("settings corrupted file survives load", () => {
    string dir = Path.Combine(Path.GetTempPath(), "DeltaPlayer-test-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    try {
        var store = new Settings(dir); File.WriteAllText(Path.Combine(dir, "settings.json"), "broken");
        Assert(store.Load().Warning != null); Assert(File.ReadAllText(Path.Combine(dir, "settings.json")) == "broken");
        store.Save(new("folder", 2)).GetAwaiter().GetResult(); Assert(store.Load().Value.Speed == 2);
    } finally { File.Delete(Path.Combine(dir, "settings.json")); Directory.Delete(dir); }
});
Test("pause and resume do not reattack interrupted note", () => {
    var cts = new CancellationTokenSource(); var player = new Player(); var clock = new FakeClock();
    clock.DuringWait = () => { if (clock.Now > .2) { player.Cancel(true); cts.Cancel(); } };
    var output = new Recording(clock); var plan = P(N(1, 0, .5), N(2, .5, 1, 62));
    var first = player.Run(plan, () => output, clock, cts.Token);
    Assert(first.Paused && first.NextIndex == 1); Assert(!output.HasHeld);
    var resumeClock = new FakeClock(); var nextOutput = new Recording(resumeClock);
    var next = new Player().Run(plan, () => nextOutput, resumeClock, default, first.NextIndex, first.Position);
    Assert(next.Completed); Assert(nextOutput.Events.Count(e => e.Kind == "down") == 1);
});
Test("native input ABI correct on x64", () => { Assert(Environment.Is64BitProcess); Assert(System.Runtime.InteropServices.Marshal.SizeOf<WindowsIO.Input>() == 40); });
Test("only ordinary player to elevated game requires elevation", () => {
    Assert(WindowsIO.PermissionProblem(7, pid => pid == null ? false : true) != null);
    Assert(WindowsIO.PermissionProblem(7, _ => false) == null);
    Assert(WindowsIO.PermissionProblem(7, _ => null) == null);
});
Test("manual keyboard or mouse input reports a resumable pause", () => {
    Assert(WindowsIO.IsHardwareKeyboardInput(0)); Assert(!WindowsIO.IsHardwareKeyboardInput(0x10));
    Assert(WindowsIO.IsHardwareMouseInput(0)); Assert(!WindowsIO.IsHardwareMouseInput(1));
    Assert(!WindowsIO.IsManualKeyboardInput(0x77, 0)); Assert(!WindowsIO.IsManualKeyboardInput(0x78, 0));
    Assert(WindowsIO.IsManualKeyboardInput(0x57, 0)); Assert(!WindowsIO.IsManualKeyboardInput(0x57, 0x10));
    var clock = new FakeClock(); var output = new Recording(clock) { PauseAt = .2 };
    var result = new Player().Run(P(N(1, 0, .5), N(2, .5, 1, 62)), () => output, clock, default);
    Assert(result.Paused && result.NextIndex == 1 && result.Position > 0 && result.PauseNotice != null);
});
Test("MIDI excessive declared payload rejected", () => {
    byte[] invalid = [0x4d,0x54,0x68,0x64,0,0,0,6,0,0,0,1,1,0xe0,0x4d,0x54,0x72,0x6b,0,0,0,6,0,0xf0,0xff,0xff,0xff,0x7f];
    Reject(() => ScoreImport.ReadMidi(invalid));
});
Test("10k-note adaptation performance", () => {
    var song = S(Enumerable.Range(0, 10000).Select(i => N(i, i * .15, i * .15 + .14, 60 + i % 12)).ToArray());
    var watch = Stopwatch.StartNew(); var result = Music.Adapt(song, 0); watch.Stop();
    Assert(result.Plan != null); Assert(result.Plan!.Notes.Length == 10000); Assert(watch.Elapsed.TotalSeconds < 5);
    Console.WriteLine($"  10000 notes: {watch.Elapsed.TotalMilliseconds:F0} ms");
});
int failed = 0;
foreach (var (name, run) in cases) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + "\n" + e); } }
Console.WriteLine($"RESULT: {cases.Count - failed}/{cases.Count} passed. No real keyboard/mouse input was sent.");
return failed == 0 ? 0 : 1;

sealed class FakeClock : IPlaybackClock
{
    public double Now { get; set; }
    public double LateAt = double.PositiveInfinity, Lateness, CancelAt = double.PositiveInfinity;
    public CancellationTokenSource? Cancel;
    public Action? DuringWait;
    public void WaitUntil(double deadline, CancellationToken cancel, Action check)
    {
        while (Now < deadline) {
            cancel.ThrowIfCancellationRequested(); check(); Now = Math.Min(deadline, Now + .005);
            if (Now >= LateAt) { Now += Lateness; LateAt = double.PositiveInfinity; }
            if (Now >= CancelAt) Cancel?.Cancel();
            DuringWait?.Invoke();
        }
        cancel.ThrowIfCancellationRequested(); check();
    }
}
sealed class Recording(FakeClock clock) : IOutput
{
    public readonly List<(string Kind, double Time)> Events = new();
    public bool HasHeld { get; private set; }
    public bool ReleaseFails;
    public double LoseFocusAt = double.PositiveInfinity, PauseAt = double.PositiveInfinity, PreparationCost;
    public void Check() { if (clock.Now >= PauseAt) throw new PlaybackPauseException("movement input"); if (clock.Now >= LoseFocusAt) throw new Exception("focus lost"); }
    public void Prepare(Fingering f) { clock.Now += PreparationCost; HasHeld = f.Modifiers != 0; Events.Add(("prepare", clock.Now)); }
    public void Down(Fingering f) { HasHeld = true; Events.Add(("down", clock.Now)); }
    public void Release() { if (ReleaseFails) throw new Exception("release failed"); if (HasHeld) Events.Add(("up", clock.Now)); HasHeld = false; }
    public void Close() => Release();
}
