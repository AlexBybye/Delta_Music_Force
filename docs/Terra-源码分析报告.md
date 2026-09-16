# Terra：三个仓库的源码分析

审阅日期：2026-09-17。以下为 Terra 的源码静态审阅结果，未运行游戏、未执行仓库测试；时间参数与游戏采样关系仍须实机验证。文中的帧级效果是待验证假设，不代表 Windows 输入已获游戏确认。

已完成只读源码审阅；未实现代码或写入外部位置。审阅基线为：

- `harmonica-auto-player`：`a14335c`（2026-09-16）
- `HarmonicaPlayer`：`3033575`（2026-09-13）
- `delta-melodica`：`0a966f0`（2026-09-15）

## 结论

适合汇总的组合是：以 `delta-melodica` 的“纯音乐领域模型、严格解析、生命周期与测试”作骨架；吸收 `harmonica-auto-player` 的 MIDI 容错、轨道/声部优先级和“单音槽位”思想；保留 `HarmonicaPlayer` 的小而明确的 TXT 简谱模式作为备用输入。不要直接继承三个项目中任何一个的实际物理时序实现。

更准确地说，三者的物理输入时序都有需要复核的问题，但原因不同：第一个把安全间隔写入音乐时间轴再按倍率播放；第二个使用固定 12 ms 提前窗口，但窗口从名义起音时间计算，调度迟到时实际间隔更短；第三个发送修饰键后直接发送音键，没有显式稳定等待。这些是源码层面的风险，实际漏音情况与游戏输入采样机制尚未实测。

## 1. ChickenD233/harmonica-auto-player

技术栈：C# / .NET 8、Avalonia 11.3、DryWetMidi 7.2、Win32 `SendInput` / `WH_KEYBOARD_LL`、`winmm` MIDI 试听。[项目文件](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/HarpAutoPlayer.csproj)

核心模块：

- [`MidiLoader.cs`](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/Midi/MidiLoader.cs)：解析 MIDI 0/1/2、按“轨道 × 声道”拆候选、以全局 TempoMap 转秒；支持 RIFF `.rmi`、UTF-8→GBK→Latin-1 轨名回退、非法元事件参数夹紧、截断文件按完整 `MTrk` 尾部修复。
- [`NoteMapper.cs`](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/Engine/NoteMapper.cs)：音高映射、基准八度自动选择、合奏压缩和前导休止裁剪。
- [`PlaybackEngine.cs`](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/Engine/PlaybackEngine.cs)：把音符编译为键盘/鼠标 down/up 事件，在专用后台线程内按 `Stopwatch` 调度；支持暂停、跳转、循环、播放中换调。
- [`InputTiming.cs`](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/Engine/InputTiming.cs)：稳健/标准/极限三档与时序诊断。
- [`InputSender.cs`](https://github.com/ChickenD233/harmonica-auto-player/blob/a14335c86a17080186bbf781f3156e65e22c4ad2/HarpAutoPlayer/Input/InputSender.cs)：扫描码模式 `SendInput`。

映射与冲突：

- 自然音 `Z X C V B N M`，高高音 C 用 `,`；左鼠标=低八度、右鼠标=高八度、中鼠标=升半音。自动基准八度通过最大化可演奏音数选取。
- `MergeVoicesByPriority` 以 25 ms 视作“同刻”，选 Rank 最小声部；被遮挡的低优先级长音在高优先级结束后补其尾部。
- 后续调度器仍会把重叠音放入单音“槽位”，顺延至前音释放后，试图避免靠压短前音腾位产生零时长。

可复用：

- 导入边界的容错和明确报错非常实用；尤其是 TempoMap 而非自行按固定 BPM 换算。
- “先将音乐冲突归约为单音，再将其映射为物理事件”的分层是对的。
- 所有中断、跳转、换谱都强制释放已持有输入，思路正确。
- 时序诊断与“不可演奏/被压缩”计数值得保留。

不应直接继承：

- `InputTiming` 虽称所有预算为“物理毫秒”，但 `BuildSchedule` 将这些毫秒放在音乐时间轴，工作线程再按 `_speed` 积分。速度大于 1 时，修饰键提前量、最短按住时间和重触发间隔仍被实际压缩。例如标准 40 ms 修饰键提前量在 5× 时约成 8 ms。这与注释的设计目标相反，是可靠性最高风险。
- 同理，`BuildSchedulePreview` 把所有事件时间除以速度，导出的宏也会压缩这些“物理”安全间隔。
- 重叠音一律顺延且保留原时值，密集和弦可让歌曲越来越滞后；应记录“延迟/丢弃/折叠”并允许用户选择策略，而不是悄然拖慢旋律。
- `MidiLoader.Parse` 全量 `File.ReadAllBytes`，没有文件大小上限；不适合作为不可信 MIDI 的直接入口。
- 诊断阈值写死为 16.7/45 ms，未跟随选定档位，可能给出误导性的“健康”结果。

## 2. lanselanlanxi-wq/HarmonicaPlayer

技术栈：C# / .NET 8 Windows、WPF、纯 Win32 P/Invoke，没有第三方 MIDI 或音频库。[项目文件](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/HarmonicaPlayer.csproj)

核心模块：

- [`ScoreParser.cs`](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/ScoreParser.cs)：有限状态 TXT 简谱解析。
- [`Program.cs`](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/Program.cs)：WPF UI、预览、倒计时、绝对时间调度、失焦停止。
- [`NativeInput.cs`](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/NativeInput.cs)：扫描码键盘与鼠标键输入、仅释放本程序记录的输入。
- [`SettingsWriter.cs`](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/SettingsWriter.cs)：后台串行保存、原子替换。
- [`Tests/Program.cs`](https://github.com/lanselanlanxi-wq/HarmonicaPlayer/blob/3033575bbfa98a20827a101bc4032951452a78d8/Tests/Program.cs)：简谱、设置和保存队列测试。

简谱、映射与调度：

- 文法为 `1..7`、`0` 休止、`【】` 高八度、`（）`/`()` 低八度、`#` 升半音；节奏模式支持 `_`、`.`、`-`/`—`，并阻止嵌套音区、孤立修饰符、超长时值等。
- 非节奏模式将空格/换行变为额外停顿；节奏模式则按 BPM 和拍数计算，忽略排版留白。
- 以绝对 `Stopwatch` 目标时间调度，超过容差即停止，避免恢复后补发堆积音符；开始前和倒计时后检查前台窗口，失焦停止。
- 每个非休止音都执行：按鼠标修饰键 → 等 12 ms → 键盘音键 → 留白前释放全部当前输入。

可复用：

- 这是最适合作为“受限简谱格式”参考的项目：语法小、错误定位精确、有音符数与文件大小上限。
- 绝对时间轴、失焦终止、默认“仅日志测试”、停止热键不可用时禁止真实演奏，都是可靠默认值。
- `SettingsWriter` 的“写入串行化 + 原子替换”可直接借鉴。

不应直接继承：

- 不支持 MIDI、轨道选择、和弦/多声部归约，因此不能作为主音乐管线。
- 12 ms 修饰键提前量远低于许多游戏的帧采样周期；修饰键和音键仍容易落在同一帧。
- 每音完整释放所有修饰键，虽简单，却增加物理事件数和快速旋律的失败概率。
- 调度循环用约 5 ms 轮询加短自旋，负载下会停止而非降级；其“停止策略”值得保留，但时序模型需重做。

## 3. gujingyun/delta-melodica

技术栈：Python、Tkinter、`mido`、`ctypes` Win32、`pystray`、Pillow；并包含 Android、网站、云端服务，但这些都超出个人离线工具的必要范围。[依赖](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/requirements.txt)

核心模块：

- [`music.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/music.py)：MIDI、两种简谱解析、单旋律归约、钢琴适配、指法表与播放计划编译。
- [`player.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/player.py)：可取消播放器、倒计时、暂停/续播、定位、运行时更新播放计划。
- [`win_input.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/win_input.py)：窗口匹配、权限不匹配提示、扫描码 `SendInput`、输入拥有权记录、全局热键及本机 MIDI 试听。
- [`score_file.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/score_file.py)：BOM UTF-8 JSON、旧格式兼容、版本验证及编辑原谱与标准音符一致性校验。
- [`test_music.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/test_music.py)、[`test_transport.py`](https://github.com/gujingyun/delta-melodica/blob/0a966f092479772862a4b30a834dc174cdd5cb2c/test_transport.py)：覆盖 MIDI 变速、单音化、输入释放、失焦、定位、实时改速/移调等。

解析、冲突与映射：

- `read_midi` 限制 10 MB、20 万事件、3 万音、30 分钟；读取全局变速表，忽略打击乐通道，并用 `(轨道、声道、音高)` 队列正确配对重复 Note On/Off。
- `monophonic` 将所有起止点切段，对每段取仍在发声的最高音；高音结束后可恢复先前低音。`piano_melody` 是更偏听感的钢琴高声部清理器。
- `parse_jianpu_space` 支持拍号外的 BPM、段落调号、反复、一二房子、同音延音、连奏、歌词位置转调和精确分数拍，并有展开上限。
- `Mapping` 不是硬编码八键：可配置八个键、基准音、左右鼠标八度偏移和中键半音；`fingerings` 先偏好无鼠标、再单修饰、最后组合修饰。
- `compile_plan` 对无法完全映射的音同音级选择最近可演奏八度，记录 `folded` 数。

可复用：

- 将 `Song / Note / Fingering / PlayNote / Plan` 做成与 UI、输入解耦的不可变数据模型，是三者中最好的核心结构。
- 在导入时严格限额、在保存时校验“文字原谱与规范化音符是否一致”、在运行时以 `run_id` 丢弃旧任务回调，成熟度很高。
- 释放顺序为音键先、修饰键后；部分 `SendInput` 失败也保留已按输入并尝试释放；失焦立刻停止。这是输入安全的最佳参考。
- 测试并非只测解析，也覆盖了暂停、定位、实时改速/移调、输入失败与焦点变化，适合作为后续验收模板。

不应直接继承：

- 对仅个人使用的离线程序，不应引入账号、云同步、聚合搜谱、网站和 Android 子系统；它们放大攻击面、生命周期和维护成本。
- `compile_plan` 的就近八度折返会改变旋律；必须改为默认显式报告并由用户选择“跳过 / 折返 / 自动移调”，不可静默发生。
- `WindowsOutput.begin` 逐条发送鼠标修饰键后立即发送音键，没有真实帧级提前量保证；`Player` 的 8 ms 轮询也偏向 UI 响应，而非游戏帧兼容。
- “最高活跃音”是合理默认，但不是通用主旋律识别；应让用户固定轨道、选择高音优先或钢琴适配策略。

## 建议的核心架构

```text
输入文件 / 简谱
  → Parser + 严格资源限制
  → Canonical Song（秒级 Note、轨道、来源）
  → 单音归约策略（显式产生决策报告）
  → InstrumentProfile 映射（精确可演奏性）
  → Physical Scheduler（物理时间事件）
  → InputSink（前台校验、SendInput、输入释放账本）
```

关键决策：

1. 采用 `Song / Note / Plan / Fingering` 这类纯模型；UI、文件、输入 API 只能位于边缘层。
2. MIDI 用全局 TempoMap/tempo table 转换为秒；限制文件大小、事件数、音符数与总时长。保留原轨道，让“自动推荐”只是建议，不是强制。
3. 单音归约必须是可切换策略：`选定轨道`、`最高活跃音`、`优先声部`、`钢琴适配`。输出报告应包括被丢弃、裁短、延迟、折返和越界的音符数。
4. 映射函数返回“精确指法 / 不可演奏原因”，默认不折返八度。折返、自动移调、跳过分别是用户可见策略。
5. 速度先将乐谱时间换成目标物理时间：`target = sourceTime / speed`；然后在物理时间轴施加修饰键提前、最短按住、重触发和释放间隔。绝不能先加安全间隔再整体除以速度。
6. 单音乐器冲突使用“物理槽位”，但为每次顺延设置最大容许滞后；超限时按策略跳过或缩短，并报告，而非无限拖后整首歌。
7. 输入端维护“实际已发送”的持有表；停止、跳转、异常、失焦、关闭都统一走幂等释放。开始前、每个事件前及修饰键与音键之间都校验前台窗口。
8. 使用可替换时钟和记录型 `InputSink` 做无游戏测试；重点断言：任意速度下修饰键提前量/最短按住时间保持物理毫秒、无重叠音键、停止后无持有输入、失焦后不再有 NoteDown。

最后，建议只面向练习、单机或自定义房间，并明确不做规避检测、反作弊绕过或隐藏自动输入的设计。
