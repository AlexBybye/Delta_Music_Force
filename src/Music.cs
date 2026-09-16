namespace DeltaPlayer;

public sealed record Tone(int Id, double Start, double End, int Pitch, int Velocity, string Source);
public sealed record Voice(string Id, string Name, Tone[] Notes);
public sealed record Song(string Title, string Hash, Voice[] Voices, double Duration, string[] Warnings);
public sealed record Fingering(int Pitch, ushort Scan, int Modifiers);
public sealed record PlannedTone(Tone Source, Fingering Finger, double Prepare, double On, double Off);
public sealed record Plan(PlannedTone[] Notes, double Duration, double Speed, int Octaves, int Missing, string Message);
public sealed record Adaptation(Plan? Plan, int VoiceIndex, int Missing, string Message);

public static class Music
{
    // Provisional compatibility budget, not a measurement of the game's sampling cycle.
    public const double Settle = .040, MinHold = .045, Gap = .020, SameKeyGap = .045, MaxLate = .050;
    private static readonly int[] Scale = [0, 2, 4, 5, 7, 9, 11, 12];
    private static readonly ushort[] Scans = [0x2c, 0x2d, 0x2e, 0x2f, 0x30, 0x31, 0x32, 0x33];
    private static readonly Dictionary<int, Fingering> Fingers = BuildFingers();

    private static Dictionary<int, Fingering> BuildFingers()
    {
        var result = new Dictionary<int, Fingering>();
        foreach (var (octave, button) in new[] { (0, 0), (-12, 1), (12, 2) })
            for (int sharp = 0; sharp <= 1; sharp++)
                for (int i = 0; i < Scale.Length; i++)
                {
                    int pitch = 60 + octave + Scale[i] + sharp;
                    var finger = new Fingering(pitch, Scans[i], button | (sharp == 1 ? 4 : 0));
                    if (!result.TryGetValue(pitch, out var prior) || Bits(finger.Modifiers) < Bits(prior.Modifiers)) result[pitch] = finger;
                }
        return result;
    }
    private static int Bits(int value) => System.Numerics.BitOperations.PopCount((uint)value);
    public static Fingering? Map(int pitch) => Fingers.GetValueOrDefault(pitch);

    public static int Recommend(Song song) => song.Voices.Select((v, i) => (v, i))
        .OrderByDescending(x => new[] { "melody", "vocal", "lead", "主旋律", "人声" }.Any(w => x.v.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        .ThenByDescending(x => Math.Min(x.v.Notes.Length, 100) + x.v.Notes.Average(n => n.Pitch) * .35)
        .First().i;

    public static Tone[] Monophonic(IEnumerable<Tone> source)
    {
        var result = new List<Tone>();
        foreach (var group in source.OrderBy(n => n.Start).GroupBy(n => n.Start))
        {
            var note = group.OrderByDescending(n => n.Pitch).ThenByDescending(n => n.Velocity).ThenBy(n => n.Id).First();
            if (result.Count > 0 && result[^1].End > note.Start) result[^1] = result[^1] with { End = note.Start };
            if (note.End > note.Start) result.Add(note);
        }
        return result.ToArray();
    }

    public static Adaptation Adapt(Song song, int voiceIndex, double requestedSpeed = 1, bool allowPartial = false)
    {
        if (!double.IsFinite(requestedSpeed) || requestedSpeed < .5 || requestedSpeed > 1.25) throw new ArgumentOutOfRangeException(nameof(requestedSpeed));
        var melody = Monophonic(song.Voices[voiceIndex].Notes);
        int octave = Enumerable.Range(-5, 11).OrderByDescending(o => melody.Count(n => Map(n.Pitch + o * 12) != null))
            .ThenBy(Math.Abs).ThenBy(o => o).First();
        int missing = melody.Count(n => Map(n.Pitch + octave * 12) == null);
        if (missing > 0 && !allowPartial) return new(null, voiceIndex, missing, "部分音符超出乐器音域，可换个声部，或仅播放可演奏部分。");
        var playable = melody.Where(n => Map(n.Pitch + octave * 12) != null).ToArray();
        if (playable.Length == 0) return new(null, voiceIndex, missing, "这个声部没有可演奏音符，请换个声部。");
        // Bounded automatic slowing: at most 15%, never silently remove dense notes.
        foreach (double factor in new[] { 1.0, .95, .90, .85 })
        {
            double speed = requestedSpeed * factor;
            var notes = Compile(playable, octave, speed);
            if (notes == null) continue;
            string message = missing > 0 ? $"仅播放可演奏部分（略过 {missing} 个超出音域的音符）" : octave != 0 ? "已适配音域" : "已准备好";
            if (factor < 1) message += " · 已稍微放慢，以保留音符";
            return new(new(notes, Math.Max(notes[^1].Off, .1 + song.Duration / speed), speed, octave, missing, message), voiceIndex, missing, message);
        }
        return new(null, voiceIndex, missing, "这段旋律过密，当前无法完整演奏。试试慢一点，或换个声部。");
    }

    public static PlannedTone[]? Compile(Tone[] notes, int octave, double speed)
    {
        var result = new List<PlannedTone>();
        double priorOff = double.NegativeInfinity;
        Fingering? prior = null;
        foreach (var (note, index) in notes.Select((n, i) => (n, i)))
        {
            var finger = Map(note.Pitch + octave * 12) ?? throw new ArgumentException("Unmapped pitch");
            double target = .1 + note.Start / speed, targetEnd = .1 + note.End / speed;
            double prepare = Math.Max(Math.Max(0, priorOff), target - (finger.Modifiers != 0 ? Settle : 0));
            double on = Math.Max(target, priorOff + (prior?.Scan == finger.Scan ? SameKeyGap : Gap));
            if (finger.Modifiers != 0) on = Math.Max(on, prepare + Settle);
            if (prior?.Modifiers != 0 && prior != null) on = Math.Max(on, priorOff + Settle);
            double off = target + (targetEnd - target) * .9;
            if (index + 1 < notes.Length)
            {
                var next = Map(notes[index + 1].Pitch + octave * 12)!;
                double space = Math.Max(finger.Scan == next.Scan ? SameKeyGap : Gap,
                    finger.Modifiers != 0 || next.Modifiers != 0 ? Settle : 0);
                off = Math.Min(off, .1 + notes[index + 1].Start / speed - space);
            }
            off = Math.Max(on + MinHold, off);
            if (on - target > MaxLate + 1e-9 || off - targetEnd > MaxLate + 1e-9) return null;
            result.Add(new(note, finger, prepare, on, off));
            priorOff = off; prior = finger;
        }
        return result.ToArray();
    }
}
