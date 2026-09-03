namespace CatClawVideo.Core.Interfaces;

/// <summary>应用主题色（5 套，与猫爪音乐一致）</summary>
public enum AppTheme
{
    Purple = 0,
    Pink = 1,
    Blue = 2,
    Orange = 3,
    Teal = 4,
}

/// <summary>深浅色模式</summary>
public enum DarkModeSetting
{
    Light = 0,
    Dark = 1,
    FollowSystem = 2,
}

/// <summary>主题服务抽象：主题色切换、深浅模式切换、资源字典刷新</summary>
public interface IThemeService
{
    /// <summary>当前主题色</summary>
    AppTheme CurrentTheme { get; }

    /// <summary>当前深浅色设置</summary>
    DarkModeSetting DarkModeSetting { get; }

    /// <summary>所有可用主题</summary>
    List<AppTheme> AvailableThemes { get; }

    /// <summary>切换主题色并持久化</summary>
    void SetTheme(AppTheme theme);

    /// <summary>设置深浅模式并持久化</summary>
    void SetDarkModeSetting(DarkModeSetting setting);

    /// <summary>应用当前主题到资源字典（刷新全部 DynamicResource 绑定）</summary>
    void ApplyTheme();

    /// <summary>最终生效的深色状态（用户设置优先，跟随系统时取系统值）</summary>
    bool IsEffectivelyDark();

    /// <summary>主题应用完成通知（页面/平台层刷新原生配色）</summary>
    event Action? Applied;
}
