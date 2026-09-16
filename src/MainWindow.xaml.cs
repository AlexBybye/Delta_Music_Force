using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeltaPlayer;

public partial class MainWindow : Window
{
    private readonly Player player = new();
    private readonly Settings settings;
    private Preferences preferences;
    private readonly string? smoke;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Song? song; private Plan? plan;
    private Task? activeUi;
    private CancellationTokenSource? importCancel;
    private bool changing, busy, preview, paused, closing, allowClose, stopReady, playReady;
    private int resumeIndex; private double resumePosition;
    private HwndSource? source; private nint hwnd;
    private string lastError = "";
    private GameWindow? lastTarget;

    public MainWindow(string? smokeDirectory = null)
    {
        smoke = smokeDirectory; settings = new(smoke == null ? null : System.IO.Path.Combine(smoke, "settings"));
        var loaded = settings.Load(); preferences = loaded.Value;
        InitializeComponent(); changing = true; Speed.SelectedIndex = preferences.Speed; changing = false;
        if (loaded.Warning != null) Status.Text = loaded.Warning;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook);
            if (smoke == null)
            {
                stopReady = WindowsIO.RegisterHotKey(hwnd, 2, 0x4000, 0x78);
                playReady = WindowsIO.RegisterHotKey(hwnd, 1, 0x4000, 0x77);
                HotkeyText.Text = $"{(playReady ? "F8 播放／暂停" : "F8 已被占用")}     {(stopReady ? "F9 停止" : "F9 已被占用，游戏播放不可用")}";
            }
        };
        timer.Tick += (_, _) =>
        {
            if (!player.Active || plan == null) return;
            var state = player.Snapshot; UpdateProgress(state.Position);
            if (state.State != "playing") Status.Text = state.State;
            else Status.Text = preview ? "正在试听 · 这是适配后的旋律" : "正在播放 · 切出游戏会自动停止";
            PlayButton.Content = !preview ? (state.State == "playing" ? "暂停" : "取消") : "播放";
        };
        timer.Start(); Closing += OnClosing;
        Loaded += async (_, _) => { if (smoke != null) await Smoke(); };
        UpdateControls();
    }
    private nint Hook(nint h, int message, nint w, nint l, ref bool handled)
    {
        if (message != 0x0312) return 0;
        if (w == 2) { RequestStop(); handled = true; }
        if (w == 1) { _ = ToggleGame(); handled = true; }
        return 0;
    }
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (busy || player.Active || player.NeedsRelease) return;
        var dialog = new OpenFileDialog { Filter = "曲谱|*.mid;*.midi;*.txt|所有文件|*.*", Title = "打开曲谱" };
        if (Directory.Exists(preferences.Folder)) dialog.InitialDirectory = preferences.Folder;
        if (dialog.ShowDialog(this) == true) await LoadSong(dialog.FileName);
    }
    public async Task LoadSong(string path)
    {
        if (busy || player.Active || player.NeedsRelease) return;
        busy = true; importCancel = new(); Status.Text = "正在读取曲谱…"; UpdateControls();
        try
        {
            var next = await Task.Run(() => ScoreImport.Load(path, importCancel.Token));
            importCancel.Token.ThrowIfCancellationRequested(); if (closing || allowClose) return;
            song = next; paused = false; resumeIndex = 0; resumePosition = 0;
            changing = true; Voices.ItemsSource = song.Voices; Voices.SelectedIndex = Music.Recommend(song); changing = false;
            SongTitle.Text = song.Title; SongTitle.ToolTip = song.Title;
            EmptyPanel.Visibility = Visibility.Collapsed; SongPanel.Visibility = Visibility.Visible; OpenTop.Visibility = Options.Visibility = Visibility.Visible;
            preferences = preferences with { Folder = System.IO.Path.GetDirectoryName(path) };
            await Rebuild(); await SaveSettings();
        }
        catch (OperationCanceledException) { Status.Text = "已取消读取曲谱。"; }
        catch (Exception e) { Report(e, "曲谱无法读取，请检查文件或换一首试试。"); }
        finally { busy = false; importCancel.Dispose(); importCancel = null; UpdateControls(); }
    }
    private async Task Rebuild(bool partial = false)
    {
        if (song == null) return;
        int voice = Math.Max(0, Voices.SelectedIndex); double speed = Speed.SelectedIndex switch { 0 => .8, 2 => 1.2, _ => 1 };
        bool wasPaused = paused; int? nextId = wasPaused && plan != null && resumeIndex < plan.Notes.Length ? plan.Notes[resumeIndex].Source.Id : null;
        double sourcePosition = plan == null ? 0 : Math.Max(0, (resumePosition - .1) * plan.Speed);
        var result = await Task.Run(() => Music.Adapt(song, voice, speed, partial));
        plan = result.Plan;
        if (plan != null && wasPaused)
        {
            resumeIndex = nextId == null ? plan.Notes.Length : Array.FindIndex(plan.Notes, n => n.Source.Id == nextId);
            if (resumeIndex < 0) resumeIndex = 0;
            resumePosition = .1 + sourcePosition / plan.Speed;
        }
        PartialButton.Visibility = result.Missing > 0 && !partial ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = result.Message + (song.Warnings.Length > 0 ? " · " + string.Join("；", song.Warnings) : "");
        UpdateProgress(wasPaused ? resumePosition : 0); DrawContour(); UpdateControls();
    }
    private async Task SaveSettings()
    {
        try { await settings.Save(preferences); }
        catch (Exception e) { lastError = e.ToString(); Status.Text += " · 设置未能保存，下次启动会使用原设置"; }
    }
    private void Report(Exception e, string? text = null) { lastError = e.ToString(); Status.Text = text == null ? e.Message : text + " " + e.Message; }
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (plan == null || player.NeedsRelease) return;
        busy = true; UpdateControls();
        try
        {
            if (player.Active || activeUi is { IsCompleted: false }) { bool wasPreview = preview; await StopAndWait(); if (wasPreview) return; }
            paused = false; resumeIndex = 0; resumePosition = 0;
            BeginOutput(true, () => new PreviewOutput(), 0);
        }
        catch (Exception error) { Report(error); }
        finally { busy = false; UpdateControls(); }
    }
    private async void PlayClick(object sender, RoutedEventArgs e) => await ToggleGame();
    private async Task ToggleGame()
    {
        if (busy || closing) return;
        if (player.Active && !preview) { player.Cancel(player.Snapshot.State == "playing"); return; }
        if (player.NeedsRelease || plan == null) return;
        if (!stopReady) { Status.Text = "停止热键 F9 被其他程序占用，请关闭占用程序后重新打开拾音。试听仍可使用。"; return; }
        busy = true; UpdateControls();
        try
        {
            if (player.Active || activeUi is { IsCompleted: false }) await StopAndWait();
            var games = WindowsIO.FindGames();
            if (games.Length == 0) { Status.Text = "没有找到三角洲游戏窗口。请先启动游戏，再点击播放。"; return; }
            GameWindow? target = games.Length == 1 ? games[0] : Targets.SelectedItem as GameWindow;
            if (target == null || !games.Any(g => g.Handle == target.Handle && g.Pid == target.Pid))
            {
                Targets.ItemsSource = games; TargetPanel.Visibility = Visibility.Visible; Status.Text = "请选择要演奏的游戏窗口，再点击播放。"; return;
            }
            lastTarget = target;
            string? permission = WindowsIO.PermissionProblem(target.Pid);
            if (permission != null)
            {
                Status.Text = permission;
                AdminButton.Visibility = Visibility.Visible;
                return;
            }
            AdminButton.Visibility = Visibility.Collapsed;
            TargetPanel.Visibility = Visibility.Collapsed;
            BeginOutput(false, () => new GameOutput(target), 3);
        }
        catch (Exception e) { Report(e); }
        finally { busy = false; UpdateControls(); }
    }
    private void BeginOutput(bool isPreview, Func<IOutput> factory, int countdown)
    {
        if (plan == null) return;
        preview = isPreview; var currentPlan = plan;
        int start = paused ? resumeIndex : 0; double position = paused ? resumePosition : 0;
        paused = false;
        var worker = player.Start(currentPlan, factory, start, position, countdown);
        activeUi = Observe(worker);
        UpdateControls();
    }
    private async Task Observe(Task<PlaybackResult> worker)
    {
        try
        {
            var result = await worker; paused = result.Paused; resumeIndex = result.NextIndex; resumePosition = result.Position;
            UpdateProgress(result.Position);
            Status.Text = result.Error ?? (result.Paused ? "已暂停 · 继续时从下一个音开始" : result.Completed ? (preview ? "试听结束" : "演奏完成") : "已停止");
        }
        catch (OperationCanceledException) { Status.Text = "已停止"; paused = false; }
        catch (Exception e) { Report(e); }
        finally { preview = false; UpdateControls(); }
    }
    private void RequestStop()
    {
        importCancel?.Cancel(); player.Cancel(); paused = false; resumeIndex = 0; resumePosition = 0;
        if (!player.Active) { UpdateProgress(0); Status.Text = "已停止"; UpdateControls(); }
    }
    private void StopClick(object sender, RoutedEventArgs e) => RequestStop();
    private async Task StopAndWait() { RequestStop(); if (activeUi != null) await activeUi; }
    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await player.RetryRelease(); Status.Text = "已释放，可以重新播放。"; }
        catch (Exception error) { Report(error); }
        finally { busy = false; UpdateControls(); }
    }
    private void AdminRestartClick(object sender, RoutedEventArgs e)
    {
        if (busy || player.Active) return;
        try
        {
            WindowsIO.RestartAsAdministrator();
            allowClose = true;
            Close();
        }
        catch (Exception error) { Report(error); }
    }
    private async void PartialClick(object sender, RoutedEventArgs e) { if (busy || player.Active) return; busy = true; UpdateControls(); try { await Rebuild(true); } catch (Exception error) { Report(error); } finally { busy = false; UpdateControls(); } }
    private void VoiceClick(object sender, RoutedEventArgs e) => VoicePanel.Visibility = VoicePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private async void VoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changing || busy || player.Active || song == null) return;
        paused = false; await ChangePlan();
    }
    private async void SpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changing || busy || player.Active || song == null) return;
        preferences = preferences with { Speed = Speed.SelectedIndex }; await ChangePlan(); await SaveSettings();
    }
    private async Task ChangePlan()
    {
        busy = true; UpdateControls();
        try { await Rebuild(); }
        catch (Exception e) { Report(e); }
        finally { busy = false; UpdateControls(); }
    }
    private void ResetSpeed(object sender, RoutedEventArgs e) => Speed.SelectedIndex = 1;
    private void UpdateControls()
    {
        bool running = player.Active, editable = !busy && !running && !player.NeedsRelease;
        OpenTop.IsEnabled = OpenEmpty.IsEnabled = editable;
        Voices.IsEnabled = Speed.IsEnabled = Options.IsEnabled = editable;
        VoiceToggle.Visibility = song?.Voices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        VoiceToggle.IsEnabled = editable; PartialButton.IsEnabled = editable;
        PreviewButton.IsEnabled = !busy && !player.NeedsRelease && plan != null;
        PreviewButton.Content = preview && running ? "结束试听" : "试听";
        PlayButton.IsEnabled = !busy && !player.NeedsRelease && plan != null;
        PlayButton.Content = running && !preview ? (player.Snapshot.State == "playing" ? "暂停" : "取消") : paused ? "继续" : "播放";
        StopButton.IsEnabled = running || paused || busy;
        RetryButton.Visibility = player.NeedsRelease ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !busy;
        AdminButton.IsEnabled = !busy && !running;
        CancelImport.Visibility = importCancel != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateProgress(double seconds)
    {
        double total = plan?.Duration ?? song?.Duration ?? 0;
        Progress.Value = total <= 0 ? 0 : Math.Clamp(seconds / total, 0, 1);
        static string Format(double s) => $"{(int)s / 60:00}:{(int)s % 60:00}";
        TimeLabel.Text = $"{Format(seconds)} / {Format(total)}";
    }
    private void DrawContour()
    {
        Contour.Children.Clear();
        if (plan == null || Contour.ActualWidth < 1) return;
        var notes = plan.Notes; int step = Math.Max(1, notes.Length / 72);
        int low = notes.Min(n => n.Finger.Pitch), high = notes.Max(n => n.Finger.Pitch);
        for (int i = 0; i < notes.Length; i += step)
        {
            var n = notes[i];
            var mark = new Rectangle { Width = Math.Max(2, Math.Min(20, (n.Off - n.On) / plan.Duration * Contour.ActualWidth)), Height = 4, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb(103, 155, 137)) };
            Canvas.SetLeft(mark, n.On / plan.Duration * Math.Max(0, Contour.ActualWidth - 20));
            Canvas.SetTop(mark, 40 - (n.Finger.Pitch - low) / (double)Math.Max(1, high - low) * 34); Contour.Children.Add(mark);
        }
    }
    private void ContourChanged(object sender, SizeChangedEventArgs e) => DrawContour();
    private void ViewportChanged(object sender, SizeChangedEventArgs e) { if (Layout != null) Layout.MinHeight = Math.Max(0, Viewport.ActualHeight - 48); }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !busy && !player.Active && !player.NeedsRelease && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) await LoadSong(files[0]); }
    private void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText($"DeltaPlayer 0.1.1\n状态: {Status.Text}\n文件: {song?.Hash}\n速度: {plan?.Speed}\n八度: {plan?.Octaves}\n拾音管理员权限: {WindowsIO.ProcessElevated()}\n游戏窗口: {lastTarget?.Title}\n游戏 PID: {lastTarget?.Pid}\n游戏管理员权限: {(lastTarget == null ? null : WindowsIO.ProcessElevated(lastTarget.Pid))}\n{lastError}\n{player.Diagnostics}"); Status.Text = "诊断信息已复制。"; }
        catch (Exception error) { Report(error, "无法访问剪贴板。"); }
    }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "打开曲谱 → 试听 → 播放。\n\n播放前请装备乐器，倒计时内切回游戏。F8 播放／暂停，F9 停止。切出游戏会自动停止。若提示权限不一致，请点击“以管理员身份重启”；游戏建议使用无边框窗口模式，并关闭聊天框、背包等界面。\n\nTXT 示例：\nBPM=120\n1 1 5 5 6 6 5- | 0 【1】 (5)\n\n0 是休止；# 升半音；_ 减半；. 附点；- 延长一拍；【】高八度；() 低八度。空格和换行不改变节奏。\n\n当前预设：中音 C4，Z X C V B N M 及逗号；鼠标左／右切换八度，中键升半音。时序效果仍需在实际游戏版本验证。",
        "使用帮助", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return; e.Cancel = true; if (closing) return; closing = true;
        try
        {
            importCancel?.Cancel(); await StopAndWait();
            if (player.NeedsRelease) { Status.Text = "仍有按键或设备未清理，请重试释放后关闭。"; return; }
            await SaveSettings(); timer.Stop();
            if (stopReady) WindowsIO.UnregisterHotKey(hwnd, 2); if (playReady) WindowsIO.UnregisterHotKey(hwnd, 1);
            source?.RemoveHook(Hook); allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        finally { closing = false; }
    }
    private async Task Smoke()
    {
        try
        {
            Directory.CreateDirectory(smoke!); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("empty");
            // UI smoke never creates a game input sink or registers global hotkeys.
            string demo = System.IO.Path.Combine(AppContext.BaseDirectory, "example.txt");
            if (!File.Exists(demo)) throw new FileNotFoundException("UI smoke requires example.txt");
            await LoadSong(demo); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("loaded");
            Width = MinWidth; Height = MinHeight; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("compact");
            Options.IsExpanded = true; VoicePanel.Visibility = Visibility.Visible; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("expanded");
            var playBottom = PlayButton.TranslatePoint(new Point(0, PlayButton.ActualHeight), SongPanel);
            if (PlayButton.ActualHeight < 30 || playBottom.Y > SongPanel.ActualHeight + 1) throw new Exception("Play control clipped");
            Viewport.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("expanded-bottom");
            await File.WriteAllTextAsync(System.IO.Path.Combine(smoke!, "result.txt"), plan != null ? "PASS: import, adaptation, empty/loaded/compact render; no game input" : "FAIL: no plan");
        }
        catch (Exception e) { await File.WriteAllTextAsync(System.IO.Path.Combine(smoke!, "result.txt"), e.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
    private void Capture(string name)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        bitmap.Render(background); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(smoke!, name + ".png")); encoder.Save(output);
    }
}
