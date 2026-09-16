using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeltaPlayer;

public sealed record GameWindow(nint Handle, int Pid, long Started, string Title)
{
    public override string ToString() => Title;
}

public static class WindowsIO
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmMouseWheel = 0x020A, WmXButtonDown = 0x020B, WmMouseHWheel = 0x020E;
    private const uint LlkhfInjected = 0x10, LlmhfInjected = 0x01;
    private static LowLevelHook? keyboardHookProc, mouseHookProc;
    private static nint keyboardHook, mouseHook;
    private static int manualInputArmed, manualInputSeen;
    [StructLayout(LayoutKind.Sequential)] public struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    private delegate bool EnumCallback(nint hwnd, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint lparam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    private delegate nint LowLevelHook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHookData { public uint VkCode, ScanCode, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hookType, LowLevelHook callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint tokenHandle, int informationClass, out TokenElevation information, int informationLength, out int returnLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint ShellExecute(nint hwnd, string operation, string file, string? parameters, string? directory, int showCommand);
    [StructLayout(LayoutKind.Sequential)] private struct TokenElevation { public int IsElevated; }

    public static GameWindow[] FindGames()
    {
        var results = new List<GameWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == Environment.ProcessId) return true;
            var text = new StringBuilder(512); GetWindowText(hwnd, text, text.Capacity);
            try
            {
                using var p = Process.GetProcessById(pid);
                string title = text.ToString(), process = p.ProcessName;
                // Keep the process check, but retain the Chinese release title as a fallback for launcher variants.
                bool knownProcess = process.Contains("deltaforce", StringComparison.OrdinalIgnoreCase) || process.Contains("dfgame", StringComparison.OrdinalIgnoreCase);
                bool knownTitle = title.Contains("三角洲行动", StringComparison.OrdinalIgnoreCase) || title.Contains("Delta Force", StringComparison.OrdinalIgnoreCase);
                if (title.Length > 0 && (knownProcess || knownTitle))
                    results.Add(new(hwnd, pid, p.StartTime.ToUniversalTime().Ticks, title));
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { }
            return true;
        }, 0);
        return results.OrderBy(x => x.Title, StringComparer.CurrentCulture).ToArray();
    }
    public static bool? ProcessElevated(int? processId = null)
    {
        nint process = OpenProcess(0x1000, false, processId ?? Environment.ProcessId);
        if (process == 0) return null;
        nint token = 0;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out token)) return null;
            return GetTokenInformation(token, 20, out TokenElevation elevation, Marshal.SizeOf<TokenElevation>(), out _) ? elevation.IsElevated != 0 : null;
        }
        finally
        {
            if (token != 0) CloseHandle(token);
            CloseHandle(process);
        }
    }
    public static string? PermissionProblem(int targetPid, Func<int?, bool?>? query = null)
    {
        query ??= ProcessElevated;
        return query(null) is false && query(targetPid) is true
            ? "游戏以管理员身份运行，而拾音当前是普通权限。Windows 会拦截模拟按键。"
            : null;
    }
    public static void EnsurePermission(GameWindow target)
    {
        string? problem = PermissionProblem(target.Pid);
        if (problem != null) throw new InvalidOperationException(problem + " 请点击“以管理员身份重启”后再播放。");
    }
    public static void RestartAsAdministrator()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序的位置。");
        string Quote(string value) => value.Length == 0 ? "\"\"" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote));
        nint result = ShellExecute(0, "runas", executable, arguments, Path.GetDirectoryName(executable), 1);
        if (result.ToInt64() <= 32) throw new Win32Exception(Marshal.GetLastWin32Error(), "管理员启动被取消或失败，请重新点击后在 Windows 弹窗中确认。");
    }
    public static void Check(GameWindow target)
    {
        if (GetForegroundWindow() != target.Handle) throw new InvalidOperationException("已切出游戏，演奏已停止。");
        GetWindowThreadProcessId(target.Handle, out int pid);
        if (pid != target.Pid) throw new InvalidOperationException("游戏窗口已关闭，请重新选择。");
    }
    public static bool IsHardwareKeyboardInput(uint flags) => (flags & LlkhfInjected) == 0;
    public static bool IsHardwareMouseInput(uint flags) => (flags & LlmhfInjected) == 0;
    public static void StartManualInputMonitor()
    {
        if (keyboardHook != 0 || mouseHook != 0) return;
        keyboardHookProc = KeyboardInput; mouseHookProc = MouseInput;
        nint module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardHookProc, module, 0);
        mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookProc, module, 0);
        if (keyboardHook == 0 || mouseHook == 0)
        {
            StopManualInputMonitor();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听键盘和鼠标输入。");
        }
    }
    public static void StopManualInputMonitor()
    {
        DisarmManualInputMonitor();
        if (keyboardHook != 0) { UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
        if (mouseHook != 0) { UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
        keyboardHookProc = null; mouseHookProc = null;
    }
    public static void DisarmManualInputMonitor()
    {
        Volatile.Write(ref manualInputArmed, 0); Interlocked.Exchange(ref manualInputSeen, 0);
    }
    public static void ArmManualInputMonitor()
    {
        if (keyboardHook == 0 || mouseHook == 0) throw new InvalidOperationException("键鼠监听未准备好，请重新打开拾音后再试。");
        Interlocked.Exchange(ref manualInputSeen, 0); Volatile.Write(ref manualInputArmed, 1);
    }
    internal static string? ConsumeManualInputProblem() => Interlocked.Exchange(ref manualInputSeen, 0) != 0
        ? "检测到键盘或鼠标操作，已暂停。松开后按 F8 继续。" : null;
    public static bool IsManualKeyboardInput(uint virtualKey, uint flags) => virtualKey is not 0x77 and not 0x78 && IsHardwareKeyboardInput(flags);
    private static nint KeyboardInput(int code, nint message, nint data)
    {
        if (code >= 0 && Volatile.Read(ref manualInputArmed) != 0 && (message == WmKeyDown || message == WmSysKeyDown))
        {
            var input = Marshal.PtrToStructure<KeyboardHookData>(data);
            if (IsManualKeyboardInput(input.VkCode, input.Flags)) Interlocked.Exchange(ref manualInputSeen, 1);
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    private static nint MouseInput(int code, nint message, nint data)
    {
        bool relevant = message is WmMouseMove or WmLButtonDown or WmRButtonDown or WmMButtonDown or WmMouseWheel or WmXButtonDown or WmMouseHWheel;
        if (code >= 0 && Volatile.Read(ref manualInputArmed) != 0 && relevant && IsHardwareMouseInput(Marshal.PtrToStructure<MouseHookData>(data).Flags))
            Interlocked.Exchange(ref manualInputSeen, 1);
        return CallNextHookEx(mouseHook, code, message, data);
    }
    public static void ValidateIdentity(GameWindow target)
    {
        using var process = Process.GetProcessById(target.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != target.Started) throw new InvalidOperationException("游戏窗口已经变化，请重新打开曲谱后再试。");
        EnsurePermission(target);
        Check(target);
        foreach (int key in new[] { 1, 2, 4, 0x5a, 0x58, 0x43, 0x56, 0x42, 0x4e, 0x4d, 0xbc, 0x10, 0x11, 0x12, 0x5b, 0x5c })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("请先松开鼠标、演奏键和 Ctrl / Alt / Shift 等组合键，再开始。");
    }
    public static void Send(ushort code, bool mouse, bool down)
    {
        Input input = mouse
            ? new() { Type = 0, Union = new() { Mouse = new() { Flags = code switch { 1 => down ? 2u : 4u, 2 => down ? 8u : 16u, 4 => down ? 32u : 64u, _ => throw new ArgumentOutOfRangeException(nameof(code)) } } } }
            : new() { Type = 1, Union = new() { Keyboard = new() { Scan = code, Flags = 8u | (down ? 0u : 2u) } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未接收演奏输入，请检查游戏与播放器的权限是否一致。");
    }
}

public sealed class GameOutput : IOutput
{
    private readonly Action check;
    private readonly Action<ushort, bool, bool> send;
    private readonly List<(ushort Code, bool Mouse)> held = new();
    public bool HasHeld => held.Count != 0;
    public GameOutput(GameWindow target) : this(() =>
    {
        WindowsIO.Check(target);
        if (WindowsIO.ConsumeManualInputProblem() is string problem) throw new PlaybackPauseException(problem);
    }, WindowsIO.Send)
    {
        WindowsIO.ValidateIdentity(target);
        WindowsIO.ArmManualInputMonitor();
    }
    public GameOutput(Action check, Action<ushort, bool, bool> send) { this.check = check; this.send = send; }
    public void Check() => check();
    private void Down(ushort code, bool mouse)
    {
        check(); held.Add((code, mouse)); // Uncertain sends must also be included in cleanup.
        send(code, mouse, true);
    }
    public void Prepare(Fingering finger)
    {
        if (HasHeld) throw new InvalidOperationException("前一个音符尚未释放。");
        foreach (ushort bit in new ushort[] { 1, 2, 4 }) if ((finger.Modifiers & bit) != 0) Down(bit, true);
    }
    public void Down(Fingering finger) => Down(finger.Scan, false);
    public void Release()
    {
        var errors = new List<string>();
        for (int i = held.Count - 1; i >= 0; i--)
        {
            var item = held[i];
            try { send(item.Code, item.Mouse, false); held.RemoveAt(i); }
            catch (Exception e) { errors.Add($"{(item.Mouse ? "鼠标" : "音键")} {item.Code}: {e.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
    }
    public void Close() { Release(); WindowsIO.DisarmManualInputMonitor(); }
}

public sealed class PreviewOutput : IOutput
{
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out nint handle, uint id, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(nint handle, uint message);
    [DllImport("winmm.dll")] private static extern uint midiOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(nint handle);
    private nint handle;
    private int? pitch;
    public bool HasHeld => pitch != null;
    public PreviewOutput()
    {
        if (midiOutOpen(out handle, uint.MaxValue, 0, 0, 0) != 0) throw new InvalidOperationException("当前无法试听，本机 MIDI 合成器不可用。仍可在游戏中播放。");
    }
    public void Check() { }
    public void Prepare(Fingering finger) { }
    public void Down(Fingering finger) { pitch = finger.Pitch; Message((uint)(0x90 | (finger.Pitch << 8) | (90 << 16))); }
    private void Message(uint message) { if (midiOutShortMsg(handle, message) != 0) throw new InvalidOperationException("试听设备发生错误。"); }
    public void Release() { if (pitch is int p) { Message((uint)(0x80 | (p << 8))); pitch = null; } }
    public void Close()
    {
        if (handle == 0) return;
        if (midiOutReset(handle) != 0) throw new InvalidOperationException("试听设备复位失败。");
        pitch = null;
        if (midiOutClose(handle) != 0) throw new InvalidOperationException("试听设备关闭失败。");
        handle = 0;
    }
}
