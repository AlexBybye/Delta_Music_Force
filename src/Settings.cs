using System.Text.Json;

namespace DeltaPlayer;

public sealed record Preferences(string? Folder = null, int Speed = 1);
public sealed class Settings
{
    private readonly string file;
    private readonly SemaphoreSlim gate = new(1, 1);
    public Settings(string? directory = null) => file = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaPlayer"), "settings.json");
    public (Preferences Value, string? Warning) Load()
    {
        if (!File.Exists(file)) return (new(), null);
        try
        {
            if (new FileInfo(file).Length > 32768) throw new FormatException();
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file)) ?? throw new FormatException();
            if (value.Speed is < 0 or > 2) throw new FormatException();
            return (value, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { return (new(), "设置无法读取，已使用默认设置。"); }
    }
    public async Task Save(Preferences value)
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value));
            File.Move(temp, file, true);
        }
        finally { gate.Release(); }
    }
}
