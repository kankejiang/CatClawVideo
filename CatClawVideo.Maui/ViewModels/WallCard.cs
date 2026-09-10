namespace CatClawVideo.Maui.ViewModels;

/// <summary>海报墙卡片：历史/收藏共用的展示模型（点击行为由 OnOpen 决定）</summary>
public sealed class WallCard
{
    public required string Title { get; init; }
    public string? Cover { get; init; }
    public string Meta { get; init; } = string.Empty;

    /// <summary>右上角角标（如「更新至12集」），空则不显示</summary>
    public string? Remark { get; init; }

    /// <summary>角标可见性（XAML 无 null 判断，直接绑定布尔）</summary>
    public bool HasRemark => !string.IsNullOrEmpty(Remark);

    public Action OnOpen { get; init; } = static () => { };
}

/// <summary>海报墙分组（CollectionView IsGrouped 头部：最近播放 / 我的收藏）</summary>
public sealed class WallSection
{
    public required string Name { get; init; }
    public required IReadOnlyList<WallCard> Items { get; init; }
}
