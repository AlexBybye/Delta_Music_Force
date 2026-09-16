using System.IO;
using System.Security.Cryptography;
using System.Text;
using Melanchall.DryWetMidi.Core;

namespace DeltaPlayer;

public static class ScoreImport
{
    public const int MaxNotes = 30_000, MaxEvents = 200_000;
    public static Song Load(string path, CancellationToken cancel = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length == 0 || file.Length > 10 * 1024 * 1024) throw new FormatException("请选择非空且小于 10 MB 的曲谱。");
        byte[] data = new byte[checked((int)file.Length)];
        file.ReadExactly(data); cancel.ThrowIfCancellationRequested();
        string hash = Convert.ToHexString(SHA256.HashData(data));
        bool midi = data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("MThd"u8);
        if (midi) return ReadMidi(data, Path.GetFileNameWithoutExtension(path), hash, cancel);
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) throw new FormatException("不是有效的标准 MIDI 文件，请打开 MIDI 或 TXT 简谱。");
        if (data.Length > 1024 * 1024) throw new FormatException("简谱不能超过 1 MB。");
        try { return ParseText(new UTF8Encoding(false, true).GetString(data), Path.GetFileNameWithoutExtension(path), hash, cancel); }
        catch (DecoderFallbackException) { throw new FormatException("简谱编码无法识别，请另存为 UTF-8 文本。"); }
    }

    public static Song ParseText(string text, string title = "简谱", string hash = "", CancellationToken cancel = default)
    {
        if (text.Length > 1024 * 1024) throw new FormatException("简谱太长，请先缩短。");
        int i = text.StartsWith('\ufeff') ? 1 : 0, octave = 0, count = 0;
        char close = '\0'; double bpm = 120, beats = 0;
        var notes = new List<Tone>(); int[] scale = [0, 2, 4, 5, 7, 9, 11];
        bool hasPriorEvent = false; int? priorTone = null;
        FormatException Error(int at, string message)
        {
            int line = 1, col = 1;
            for (int j = 0; j < Math.Min(at, text.Length); j++) { if (text[j] == '\n') { line++; col = 1; } else col++; }
            return new FormatException($"第 {line} 行、第 {col} 列：{message}");
        }
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (text.AsSpan(i).StartsWith("BPM=", StringComparison.OrdinalIgnoreCase))
        {
            int start = i += 4;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            if (!int.TryParse(text[start..i], out var value) || value < 20 || value > 300) throw Error(start, "速度标记应为 BPM=20 至 BPM=300。");
            bpm = value;
            if (i < text.Length && !char.IsWhiteSpace(text[i])) throw Error(i, "速度标记后需要空格或换行。");
        }
        while (i < text.Length)
        {
            cancel.ThrowIfCancellationRequested(); char c = text[i];
            if (char.IsWhiteSpace(c) || c == '|') { i++; continue; }
            // Chinese scores commonly write a standalone long dash after a note, for example: "1 — —".
            // It extends the immediately preceding note or rest by one beat; it is not a missing new note.
            if (c is '-' or '—' or '–')
            {
                if (!hasPriorEvent) throw Error(i, "延音线前需要一个音符或休止符。");
                double extensionEnd = (beats + 1) * 60 / bpm;
                if (extensionEnd > 1800) throw Error(i, "曲谱不能超过 30 分钟。");
                if (priorTone is int tone) notes[tone] = notes[tone] with { End = extensionEnd };
                beats++;
                i++;
                continue;
            }
            if (c is '(' or '（' or '【')
            {
                if (close != '\0') throw Error(i, "音区括号不能嵌套。");
                close = c == '(' ? ')' : c == '（' ? '）' : '】'; octave = c == '【' ? 12 : -12; i++; continue;
            }
            if (c is ')' or '）' or '】')
            {
                if (c != close) throw Error(i, "音区括号不匹配。");
                close = '\0'; octave = 0; i++; continue;
            }
            int offset = i; bool sharp = c == '#';
            if (sharp) i++;
            if (i >= text.Length || text[i] < '0' || text[i] > '7') throw Error(offset, "这里需要 0 至 7 的音符。");
            int degree = text[i++] - '0';
            if (degree == 0 && (sharp || octave != 0)) throw Error(offset, "休止符不能升调或放在音区括号内。");
            double duration = 1; int reductions = 0;
            while (i < text.Length && text[i] == '_') { if (++reductions > 2) throw Error(i, "最多两个减时符号。"); duration /= 2; i++; }
            if (i < text.Length && text[i] == '.') { duration *= 1.5; i++; }
            while (i < text.Length && text[i] is '-' or '—') { duration++; i++; if (duration > 64) throw Error(offset, "单个音符不能超过 64 拍。"); }
            if (++count > MaxNotes) throw Error(offset, "音符和休止符不能超过 30000 个。");
            double startTime = beats * 60 / bpm; beats += duration; double end = beats * 60 / bpm;
            if (end > 1800) throw Error(offset, "曲谱不能超过 30 分钟。");
            hasPriorEvent = true;
            priorTone = degree == 0 ? null : notes.Count;
            if (degree != 0) notes.Add(new(count, startTime, end, 60 + octave + scale[degree - 1] + (sharp ? 1 : 0), 90, $"字符 {offset + 1}"));
        }
        if (close != '\0') throw Error(text.Length, "音区括号没有闭合。");
        if (notes.Count == 0) throw new FormatException("曲谱里没有可播放的音符。");
        return new(title, hash, [new("text", "简谱", notes.ToArray())], beats * 60 / bpm, []);
    }

    public static Song ReadMidi(byte[] data, string title = "MIDI", string hash = "", CancellationToken cancel = default)
    {
        if (data.Length > 10 * 1024 * 1024) throw new FormatException("MIDI 不能超过 10 MB。");
        using var stream = new MemoryStream(data, false);
        using var reader = MidiFile.ReadLazy(stream, new ReadingSettings
        {
            TextEncoding = Encoding.UTF8,
            InvalidChunkSizePolicy = InvalidChunkSizePolicy.Abort,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Abort,
            InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.Abort,
            InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.Abort,
            UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Abort,
            StopReadingOnExpectedTrackChunksCountReached = false,
            ReaderSettings = new ReaderSettings { BytesPacketMaxLength = 65536 }
        });
        int track = -1, events = 0, declared = -1, ppq = 0, bad = 0, noteId = 0;
        long tick = 0, lastTick = 0;
        var names = new Dictionary<int, string>();
        var active = new Dictionary<(int, int, int), Queue<(long Tick, int Velocity, int Id)>>();
        var paired = new List<(int Track, int Channel, int Pitch, long Start, long End, int Velocity, int Id)>();
        var tempos = new List<(long Tick, long Tempo, int Order)> { (0, 500000, -1) };
        foreach (var token in reader.EnumerateTokens())
        {
            cancel.ThrowIfCancellationRequested();
            switch (token)
            {
                case FileHeaderToken header:
                    if ((int)header.FileFormat > 1 || header.TimeDivision is not TicksPerQuarterNoteTimeDivision division) throw new FormatException("暂不支持独立多序列或 SMPTE MIDI，请转换为标准 MIDI 0/1。");
                    ppq = division.TicksPerQuarterNote; declared = header.TracksNumber;
                    if (ppq <= 0 || declared < 1 || declared > 128) throw new FormatException("MIDI 的速度或音轨数量无效。");
                    break;
                case ChunkHeaderToken chunk:
                    if (chunk.ChunkContentSize > data.Length || token.Position + token.Length + chunk.ChunkContentSize > data.Length) throw new FormatException("MIDI 数据块不完整。");
                    if (chunk.ChunkId == "MTrk") { track++; tick = 0; if (track >= 128) throw new FormatException("音轨太多。"); }
                    break;
                case MidiEventToken item:
                    if (++events > MaxEvents) throw new FormatException("MIDI 事件过多，请先精简曲谱。");
                    tick = checked(tick + item.Event.DeltaTime); lastTick = Math.Max(lastTick, tick);
                    if (item.Event is SetTempoEvent tempo) { if (tempo.MicrosecondsPerQuarterNote <= 0) throw new FormatException("MIDI 含无效速度。"); tempos.Add((tick, tempo.MicrosecondsPerQuarterNote, events)); }
                    if (item.Event is SequenceTrackNameEvent name) names[track] = name.Text.Length > 100 ? name.Text[..100] : name.Text;
                    if (item.Event is NoteOnEvent on && (int)on.Channel != 9 && (int)on.Velocity > 0)
                    {
                        var key = (track, (int)on.Channel, (int)on.NoteNumber);
                        if (!active.TryGetValue(key, out var queue)) active[key] = queue = new();
                        queue.Enqueue((tick, (int)on.Velocity, ++noteId));
                        if (noteId > MaxNotes) throw new FormatException("MIDI 音符超过 30000 个，请先精简。");
                    }
                    else if (item.Event is NoteOffEvent || item.Event is NoteOnEvent { Velocity: var velocity } && (int)velocity == 0)
                    {
                        var channel = (ChannelEvent)item.Event;
                        int pitch = item.Event is NoteOffEvent off ? (int)off.NoteNumber : (int)((NoteOnEvent)item.Event).NoteNumber;
                        if ((int)channel.Channel == 9) break;
                        var key = (track, (int)channel.Channel, pitch);
                        if (active.TryGetValue(key, out var queue) && queue.TryDequeue(out var start) && tick > start.Tick)
                            paired.Add((track, (int)channel.Channel, pitch, start.Tick, tick, start.Velocity, start.Id));
                        else bad++;
                    }
                    break;
            }
        }
        if (ppq == 0 || track + 1 != declared) throw new FormatException("MIDI 音轨数据不完整。");
        var sorted = tempos.OrderBy(t => t.Tick).ThenBy(t => t.Order).ToArray();
        var times = new double[sorted.Length];
        for (int j = 1; j < sorted.Length; j++) times[j] = times[j - 1] + (sorted[j].Tick - sorted[j - 1].Tick) * (double)sorted[j - 1].Tempo / ppq / 1_000_000;
        double Seconds(long position)
        {
            int lo = 0, hi = sorted.Length;
            while (lo + 1 < hi) { int mid = (lo + hi) / 2; if (sorted[mid].Tick <= position) lo = mid; else hi = mid; }
            return times[lo] + (position - sorted[lo].Tick) * (double)sorted[lo].Tempo / ppq / 1_000_000;
        }
        double duration = Seconds(lastTick);
        if (!double.IsFinite(duration) || duration > 1800) throw new FormatException("MIDI 不能超过 30 分钟。");
        if (paired.Count == 0) throw new FormatException("没有找到旋律音符，打击乐声道不会导入。");
        var voices = paired.GroupBy(n => (n.Track, n.Channel)).Select((g, index) => new Voice($"{g.Key.Track}:{g.Key.Channel}",
            string.IsNullOrWhiteSpace(names.GetValueOrDefault(g.Key.Track)) ? $"声部 {index + 1}" : names[g.Key.Track],
            g.Select(n => new Tone(n.Id, Seconds(n.Start), Seconds(n.End), n.Pitch, n.Velocity, $"轨 {n.Track + 1} / 音符 {n.Id}")).OrderBy(n => n.Start).ThenBy(n => n.Id).ToArray())).ToArray();
        bad += active.Values.Sum(q => q.Count);
        return new(title, hash, voices, duration, bad > 0 ? [$"略过 {bad} 个未完整配对的音符事件"] : []);
    }
}
