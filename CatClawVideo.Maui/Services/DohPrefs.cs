using CoreDoh = CatClawVideo.Core.Services.Doh;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// DoH 选择的持久化（对位 <c>HawkConfig.DOH_URL</c>：0=关闭，n&gt;0 用列表第 n-1 项）。
/// Core 不读 Preferences，所以这里既是存储也是「订阅重置后别把 0 当脏值丢掉」的守门人。
/// </summary>
public static class DohPrefs
{
    const string Key = "doh_selector";

    public static int Load()
    {
        int v;
        try { v = Preferences.Default.Get(Key, 0); } catch { v = 0; }
        if (v < 0) v = 0;
        // 越界（服务商列表变短了）夹回最后一项，而不是静默变「关闭」—— 用户可能确实想开着
        var count = CoreDoh.Endpoints.Count;
        if (v > count) v = count;
        CoreDoh.Selector = v;
        return v;
    }

    public static void Save(int selector)
    {
        try { Preferences.Default.Set(Key, selector); } catch { }
        CoreDoh.Selector = selector;
    }

    /// <summary>点一下轮一档：关闭 → 1 → 2 → … → 关闭。</summary>
    public static int Next(int current)
    {
        var count = CoreDoh.Endpoints.Count;
        if (count == 0) return 0;
        return current >= count ? 0 : current + 1;
    }

    public static string Label(int selector)
    {
        if (selector <= 0) return "关闭";
        var list = CoreDoh.Endpoints;
        var idx = selector - 1;
        if (idx >= list.Count) return "越界（已夹回）";
        CoreDoh.Selector = selector;
        return list[idx].Name;
    }
}
