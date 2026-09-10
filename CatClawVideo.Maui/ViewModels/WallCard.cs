using System.ComponentModel;

using System.Collections;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>海报墙卡片：历史/收藏共用的展示模型（点击行为由 OnOpen 决定）</summary>
public sealed class WallCard : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public string? Cover { get; init; }
    public string Meta { get; init; } = string.Empty;

    /// <summary>右上角角标（如「更新至12集」），空则不显示</summary>
    public string? Remark { get; init; }

    /// <summary>角标可见性（XAML 无 null 判断，直接绑定布尔）</summary>
    public bool HasRemark => !string.IsNullOrEmpty(Remark);

    public Action OnOpen { get; init; } = static () => { };

    /// <summary>携带的负载（历史页删除模式用来回查数据库 Id）</summary>
    public object? Tag { get; init; }

    private bool _selectMode;
    /// <summary>批量删除模式：卡片左上角显示勾选圈</summary>
    public bool SelectMode
    {
        get => _selectMode;
        set { if (_selectMode == value) return; _selectMode = value; PropertyChanged?.Invoke(this, new(nameof(SelectMode))); }
    }

    private bool _isSelected;
    /// <summary>批量删除模式下是否被勾选</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 海报墙分组（CollectionView IsGrouped 头部：最近播放 / 我的收藏）。
/// ⚠️ 必须实现 IEnumerable&lt;T&gt;：CollectionView 分组直接枚举组对象取条目，
/// 只暴露 Items 属性的话只渲染组头、组内条目为空（2026-09-10 收藏页踩坑）。
/// </summary>
public sealed class WallSection : IEnumerable<WallCard>
{
    public required string Name { get; init; }
    public required IReadOnlyList<WallCard> Items { get; init; }

    public IEnumerator<WallCard> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
