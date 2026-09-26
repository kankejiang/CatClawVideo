using System.Diagnostics;
using CatClawVideo.Core.Services;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 弹幕浮层（对位 TVBox <c>master.flame.danmaku.DanmakuView</c> 的调度面）。
/// <para>上游那个库没有 .NET 绑定，所以这里只把它**可移植的调度语义**搬过来：分轨（lane）占位、
/// 右滚/左滚/顶/底四类、密度阈值丢弃、定点弹幕定时消失。描边用 <see cref="Shadow"/> 近似
/// （原生库是逐字 stroke，视觉略糊但不会白字压白底看不见）。</para>
/// <para>本控件只吃「已解析好的 <see cref="DanmuCue"/> 列表 + 一个播放钟」，
/// 不持有任何播放器引用 —— 位置用 <see cref="SyncPosition"/> 喂进来，中间用本地秒表插值，
/// 所以 30fps 推进不会被进度轮询节流。</para>
/// </summary>
public sealed class DanmakuOverlay : ContentView
{
    private const double ScrollSpeed = 110;      // px/s，右滚/左滚
    private const double FixedSeconds = 4.0;     // 顶/底弹幕停留时长
    private const double FrameSeconds = 1 / 30.0;
    private const int MaxOnScreen = 60;

    private readonly AbsoluteLayout _canvas = new();
    private readonly List<DanmuCue> _cues = [];
    private readonly List<Item> _live = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _cueClock;           // 上次同步到的播放位置（秒）
    private double _cueClockAt;         // 那一刻的本地秒表读数
    private bool _playing;
    private int _cursor;                // _cues 里下一条待投喂的下标
    private double[] _laneFreeAt = [];  // 每条轨道上「最后一条弹幕尾巴离开」的时刻
    private int _laneCount = 6;

    /// <summary>弹幕密度：0.25 / 0.5 / 0.75 / 1.0。低于 1 时按轨道拥挤程度丢弃（与 TVBox 同思路）。</summary>
    public double Density { get; set; } = 1.0;

    /// <summary>字号倍率（TVBox 是 0.5x/1x/2x）。</summary>
    public double FontScale { get; set; } = 1.0;

    public DanmakuOverlay()
    {
        InputTransparent = true;
        // 开关跨启动记住：默认关 —— 网盘源的 danmaku 字段是宿主钩子，开了才会去 GET 它
        _on = Preferences.Default.Get("catclaw.danmu_on", false);
        Density = Preferences.Default.Get("catclaw.danmu_density", 1.0);
        FontScale = Preferences.Default.Get("catclaw.danmu_fontscale", 1.0);
        IsVisible = _on;
        Content = _canvas;
    }

    private IDispatcherTimer? _timer;

    void StartTimer()
    {
        if (_timer is null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);   // 30fps：弹幕不需要 60，省调度开销
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
    }

    sealed class Item(DanmuCue cue, Label label, double bornAt, double laneY, double textWidth, bool scrolling)
    {
        public DanmuCue Cue { get; } = cue;
        public Label Label { get; } = label;
        public double BornAt { get; } = bornAt;
        public double LaneY { get; } = laneY;
        public double TextWidth { get; } = textWidth;
        public bool Scrolling { get; } = scrolling;
        public bool Dead { get; set; }
    }

    /// <summary>开关：关掉时立即清空在屏弹幕，避免「关了还在飘」。</summary>
    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            IsVisible = value;
            Preferences.Default.Set("catclaw.danmu_on", value);
            if (value) StartTimer();
            else { _timer?.Stop(); ClearAll(); }
        }
    }
    private bool _on;

    public void SetCues(IReadOnlyList<DanmuCue> cues)
    {
        ClearAll();
        _cues.Clear();
        _cues.AddRange(cues);
        _cursor = 0;
        if (_on) StartTimer();   // 跨启动记住开着时，构造期 Dispatcher 还没就绪，到这里才起帧定时器
    }

    public void ClearAll()
    {
        foreach (var i in _live) _canvas.Remove(i.Label);
        _live.Clear();
        _laneFreeAt = [];
    }

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        var now = _clock.Elapsed.TotalSeconds;
        _cueClock = CurrentTime();       // 冻结前先把手上的钟走到位
        _cueClockAt = now;
        _playing = playing;
    }

    /// <summary>播放器进度同步。跳带（差得超过 1.5s）时清屏并把投喂指针二分到位。</summary>
    public void SyncPosition(double seconds)
    {
        if (Math.Abs(seconds - CurrentTime()) < 1.5)
        {
            _cueClock = seconds;
            _cueClockAt = _clock.Elapsed.TotalSeconds;
            return;
        }
        _cueClock = seconds;
        _cueClockAt = _clock.Elapsed.TotalSeconds;
        ClearAll();
        _cursor = LowerBound(seconds);
    }

    int LowerBound(double seconds)
    {
        int lo = 0, hi = _cues.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_cues[mid].TimeSeconds < seconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    double CurrentTime() =>
        _cueClock + (_playing ? _clock.Elapsed.TotalSeconds - _cueClockAt : 0);

    /// <summary>由页面按帧驱动（或自挂定时器）。</summary>
    public void Tick()
    {
        if (!IsVisible || _cues.Count == 0) return;
        var now = CurrentTime();
        SpawnDue(now);
        Advance(now);
    }

    void SpawnDue(double now)
    {
        while (_cursor < _cues.Count && _cues[_cursor].TimeSeconds <= now)
        {
            var cue = _cues[_cursor++];
            // 落后超过 1.5s 的直接丢（同步慢/刚切集时不要一次性炸出整屏）
            if (now - cue.TimeSeconds > 1.5) continue;
            if (_live.Count >= MaxOnScreen) continue;
            if (!TrySpawn(cue, now)) continue;
        }
    }

    bool TrySpawn(DanmuCue cue, double now)
    {
        var area = EnsureLanes();
        if (area <= 0) return false;
        var size = Math.Max(11f, cue.Size * 0.8f * (float)FontScale);
        var text = cue.Text.Length == 0 ? " " : cue.Text;
        var w = EstimateWidth(text, size);
        var scrolling = !cue.IsFixed;

        var lane = PickLane(now, scrolling ? (Bounds.Width + w) / ScrollSpeed : FixedSeconds, area);
        // 挤满就丢：叠成一坨的弹幕不可读，宁缺（TVBox 同取舍， Density 决定丢得多狠）
        if (lane < 0) return false;
        if (Density < 1.0 && _laneFreeAt[lane] > now + 0.35) return false;
        Place(cue, text, now, lane, size, w, scrolling, area);
        return true;
    }

    int PickLane(double now, double needsSeconds, double area)
    {
        int best = -1;
        double bestFree = double.MaxValue;
        for (int i = 0; i < _laneCount; i++)
        {
            if (_laneFreeAt[i] <= now) return i;
            if (_laneFreeAt[i] < bestFree) { bestFree = _laneFreeAt[i]; best = i; }
        }
        // 拥挤：只有最空的轨道留有余量时才投，否则交给密度阈值丢弃
        return bestFree - now > needsSeconds * 2.5 ? best : -1;
    }

    void Place(DanmuCue cue, string text, double now, int lane, double size, double w, bool scrolling, double area)
    {
        var laneHeight = area / _laneCount;
        var y = cue.IsFixed && lane >= _laneCount / 2
            ? area - (lane - _laneCount / 2 + 1) * laneHeight          // 底部轨道从下往上占
            : lane * laneHeight;

        var label = new Label
        {
            Text = text,
            FontSize = size,
            TextColor = Color.FromRgb((int)(cue.Color >> 16 & 0xFF), (int)(cue.Color >> 8 & 0xFF), (int)(cue.Color & 0xFF)),
            Shadow = new Shadow { Brush = Color.FromRgb((int)(cue.ShadowColor >> 16 & 0xFF), (int)(cue.ShadowColor >> 8 & 0xFF), (int)(cue.ShadowColor & 0xFF)), Radius = 2, Offset = new Point(0.8, 0.8) },
            ZIndex = cue.IsFixed ? 1 : 0,
        };
        AbsoluteLayout.SetLayoutBounds(label, new Rect(0, y, w + 8, laneHeight));
        // LayoutFlags 默认就是 None（绝对像素定位），弹幕正是不要按比例缩放
        label.TranslationX = scrolling ? Bounds.Width : 0;
        _canvas.Add(label);
        _live.Add(new Item(cue, label, now, y, w, scrolling));
        _laneFreeAt[lane] = now + (scrolling ? (Bounds.Width + w) / ScrollSpeed : FixedSeconds);
    }

    void Advance(double now)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var it = _live[i];
            var age = now - it.BornAt;
            if (it.Scrolling)
            {
                var dir = it.Cue.IsReverse ? 1 : -1;
                it.Label.TranslationX = dir > 0 ? -it.TextWidth + age * ScrollSpeed : Bounds.Width - age * ScrollSpeed;
                if (it.Label.TranslationX < -it.TextWidth - 4) it.Dead = true;
            }
            else if (age > FixedSeconds) it.Dead = true;

            if (it.Dead || age > 120)
            {
                _canvas.Remove(it.Label);
                _live.RemoveAt(i);
            }
        }
    }

    double EnsureLanes()
    {
        var area = Height;
        if (area <= 1) return 0;
        var lanes = Math.Max(3, (int)(area / 34.0));
        if (lanes != _laneCount || _laneFreeAt.Length != lanes)
        {
            var next = new double[lanes];
            Array.Copy(_laneFreeAt, next, Math.Min(_laneFreeAt.Length, lanes));
            _laneFreeAt = next;
            _laneCount = lanes;
        }
        return area;
    }

    /// <summary>
    /// 宽度估算：CJK 一个字约 1em，ASCII 约 0.55em。
    /// <para>MAUI 没有跨平台文本测量 API，为一条弹幕去跑一次 native handler 不值得 ——
    /// 估宽只影响轨道占用与出场点，差 10% 视觉上看不出来。</para>
    /// </summary>
    static double EstimateWidth(string text, double size)
    {
        double units = 0;
        foreach (var c in text) units += c > 0x2E80 ? 1.0 : 0.55;
        return Math.Max(size, units * size);
    }
}
