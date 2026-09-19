using System.Collections;

namespace CatClawVideo.Maui.Services;

/// <summary>可聚焦的海报卡片（<c>WallCard</c> 等实现；只有 <c>IsFocused</c> 一处的约定）。</summary>
public interface IFocusableCard
{
    bool IsFocused { get; set; }
}

/// <summary>
/// 海报墙（<see cref="CollectionView"/> + <c>GridItemsLayout</c>）的遥控器焦点：
/// 网格移动 + 滚动跟随 + 高亮渲染。
///
/// <para>历史 / 收藏两页的海报墙结构完全一致（同一套 <c>WallCard</c>、同一套
/// <c>PosterLayoutHelper</c> 定列数、同样的 <c>IsFocused</c> 描边触发器），
/// 焦点逻辑抽到这里共用 —— 否则两页各写一份网格移动，改一处必然漏一处。</para>
///
/// <para><b>越界即交还</b>：某方向走到边界时 <see cref="Move"/> 返回 <c>false</c>，
/// 按键继续冒泡到主壳层顶栏，于是形成「海报墙 → 顶栏」的走出路径
/// （与首页海报墙的处理一致）。</para>
///
/// <para><b>列数</b>以 <c>ItemsLayout.Span</c> 为准：<c>PosterLayoutHelper.Apply</c>
/// 每次窗口变化都会按可用空间重算并写回 Span，所以这里不必自己记。</para>
/// </summary>
public sealed class PosterWallFocus
{
    private readonly CollectionView _wall;

    public PosterWallFocus(CollectionView wall) => _wall = wall;

    /// <summary>当前焦点项下标；<c>-1</c> = 尚未定位（本页未持有焦点）。</summary>
    public int Index { get; private set; } = -1;

    /// <summary>本页是否持有焦点。<c>false</c> 时一律不消费按键（隐藏的 tab 页正是靠它闭嘴）。</summary>
    public bool Engaged { get; private set; }

    /// <summary>列数：读 <c>PosterLayoutHelper</c> 算好的 Span。</summary>
    private int Columns => _wall.ItemsLayout is GridItemsLayout g ? Math.Max(1, g.Span) : 1;

    /// <summary>卡片集合（<c>ItemsSource</c> 可能被置 null，统一按空集合处理）。</summary>
    private List<IFocusableCard> Cards =>
        (_wall.ItemsSource as IEnumerable)?.OfType<IFocusableCard>().ToList() ?? [];

    /// <summary>当前焦点卡片（无焦点 / 空列表时为 <c>null</c>，调用方自行判断类型）。</summary>
    public object? Current
    {
        get
        {
            var cards = Cards;
            return Index >= 0 && Index < cards.Count ? cards[Index] : null;
        }
    }

    /// <summary>
    /// 外层把焦点送进来（选完 tab / 顶栏按 ↓ / OK）：点亮**记住的那一项**（首次进入为第 0 项），
    /// 并把它滚进可视区。
    ///
    /// <para><b>数据还没到也要接住</b>：切 tab 时列表是异步加载的，此时
    /// <c>FocusContent()</c> 已经调过来了，但卡片一张都没有。直接 return 的话焦点根本没建立，
    /// 用户接着按方向键会全部落空、按键冒泡到顶栏（观感：<b>焦点跑到顶栏 tab 上去了</b>，
    /// 2026-09-19 用户截图）。所以这里先记一笔「待点亮」，列表到齐后由
    /// <see cref="Refresh"/> 自动补上。</para>
    /// </summary>
    public void Focus()
    {
        var cards = Cards;
        if (cards.Count == 0)
        {
            _focusPending = true;
            return;
        }

        _focusPending = false;
        Engaged = true;
        Index = Math.Clamp(Index < 0 ? 0 : Index, 0, cards.Count - 1);
        Render(cards);
        Scroll();
    }

    /// <summary>焦点已请求、但列表当时还空着（数据未到）——到齐后补点亮。</summary>
    private bool _focusPending;

    /// <summary>外层把焦点收走（顶栏按 ↑ / ← / →）：熄掉高亮，并停止消费按键。</summary>
    public void Blur()
    {
        _focusPending = false;   // 用户已经走开：数据到齐也不该再把焦点抢回来
        Engaged = false;
        foreach (var c in Cards) c.IsFocused = false;
    }

    /// <summary>
    /// 方向键移动。返回 <c>false</c> = 本方向已到边界（调用方应把按键交还外层）。
    /// </summary>
    public bool Move(RemoteKey dir)
    {
        if (!Engaged) return false;

        var cards = Cards;
        if (cards.Count == 0) return false;

        int cols = Columns;
        int target = dir switch
        {
            // 行首往左 / 首行往上 → 交还外层（上方是顶栏）
            RemoteKey.Left => Index % cols == 0 ? -1 : Index - 1,
            RemoteKey.Up => Index - cols < 0 ? -1 : Index - cols,
            // 末项往右 / 最后一行往下 → 交还外层
            RemoteKey.Right => Index >= cards.Count - 1 ? -1 : Index + 1,
            RemoteKey.Down => Index + cols > cards.Count - 1 ? -1 : Index + cols,
            _ => -1,
        };
        if (target < 0) return false;

        Index = target;
        Render(cards);
        Scroll();
        return true;
    }

    /// <summary>
    /// 数据重载后调用：把索引夹回有效范围并重画 —— 列表可能变短甚至变空
    /// （历史删记录、收藏取消收藏都会重建 ItemsSource）。
    /// </summary>
    public void Refresh()
    {
        var cards = Cards;

        // 进页时列表还没加载完（Focus 落空）→ 现在到齐了，把焦点补上。
        // 没有这一步就会出现「先按回车无反应、第二次才有焦点」。
        if (cards.Count > 0 && _focusPending)
        {
            Focus();
            return;
        }

        if (cards.Count == 0)
        {
            Index = -1;
            return;
        }

        Index = Math.Clamp(Index < 0 ? 0 : Index, 0, cards.Count - 1);
        if (Engaged) Render(cards);
    }

    private void Render(List<IFocusableCard> cards)
    {
        for (int i = 0; i < cards.Count; i++) cards[i].IsFocused = i == Index;
    }

    /// <summary>把焦点项滚进可视区（否则遥控器移动时焦点会跑出屏幕）。</summary>
    private void Scroll()
    {
        try
        {
            if (Index >= 0)
                _wall.ScrollTo(Index, position: ScrollToPosition.MakeVisible, animate: false);
        }
        catch { }
    }
}
