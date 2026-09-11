using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页：MacCMS 真实源（分类 + 影片列表）。
/// 海报卡点击 → 观看页（携带源/影片参数拉真实详情与选集）。
/// </summary>
public partial class HomePage : ContentView, ITabView
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // 分类集合变化 / 选中项变化时刷新 chip 高亮
        _vm.Categories.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.SelectedCategoryId))
                MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
        };

        // 海报墙自适应的触发点（多点兜底）：
        // 启动时首次布局发生在启动图阶段，窗口尺寸/系统栏 insets 尚未最终确定，
        // 量到的可用高度偏小（实测 10 列/1 行 + 底部大片空白），且此后未必再有
        // SizeChanged 兜底 → 只能等用户导航一次才恢复。故除 SizeChanged 外，
        // 再挂安全区变化、tab 显示与三次延迟重算，保证启动后自愈。
        PosterGrid.SizeChanged += (_, _) => UpdatePosterLayout();
        SafeAreaHelper.SafeAreaChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePosterLayout);
        foreach (var delay in new[] { 600, 1500, 3000 })
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                MainThread.BeginInvokeOnMainThread(UpdatePosterLayout);
            });
    }

    /// <summary>
    /// 海报墙自适应（横屏的核心修复）：
    /// ① 卡片高度 = 海报墙可视高度 − 标题/年份两行标签(约 40dp)。此前写死 190dp，而横屏
    ///    可用高度只有 ~110dp，卡片被裁掉一半、下方还留出大片空白（行高与卡片高度不成整数倍）。
    ///    让「卡片 + 标签」正好占满可视高度，裁切与空白同时消失，其余行靠滚动查看。
    /// ② 列数按「单元格宽高比 ≈ 海报 2:3」反推。原先 Span=6 写死（与注释"安卓横屏 4 列"不符），
    ///    在横屏窄高度下卡片会被拉成近似方形。
    /// </summary>
    private void UpdatePosterLayout()
    {
        try
        {
            var w = PosterGrid.Width;
            var h = PosterGrid.Height;
            if (w <= 0 || h <= 0) return;

#if WINDOWS
            // 桌面端可视区大：海报上限放宽到 300（约 200 宽），首屏更大更好看（2026-09-12 用户要求）
            PosterLayoutHelper.Apply(PosterGrid, w, h, cap: 300);
#else
            var cardH = Math.Clamp(h - 40, 64, 190);
            // 手机横屏垂直空间小：单排放满到底——扣掉底部手势条 inset 与标题/年份块(~46)，
            // 否则卡片高度比可用空间小，底部永远剩一块空白（2026-09-11 真机实测）
            if (h < 400) cardH = Math.Max(64, h - SafeAreaHelper.BottomInset - 46);
            // ⚠️ 必须写「应用级」资源：页级 DynamicResource 的更新在 Android 的
            // DataTemplate 里不生效（三种公式渲染结果纹丝不动的踩坑实录），
            // 应用级字典的变更才会传播到已实例化的模板项。
            if (Application.Current is not null)
                Application.Current.Resources["PosterCardHeight"] = cardH;

            var target = cardH * 2.0 / 3.0 + 12;   // 单元格目标宽（海报 2:3）+ 列间距
            var span = (int)Math.Clamp(Math.Round((w + 12) / target), 3, 12);
            if (PosterGrid.ItemsLayout is GridItemsLayout g && g.Span != span)
                g.Span = span;
            Android.Util.Log.Info("PosterLayout",
                $"w={w:F0} h={h:F0} cardH={cardH:F0} top={SafeAreaHelper.TopInset:F0} bottom={SafeAreaHelper.BottomInset:F0}" +
                $" parent={(Parent as VisualElement)?.Height:F0} win={Window?.Height:F0}");
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Home] 海报墙自适应失败: {ex.Message}");
        }
    }

    public Task OnTabShownAsync()
    {
        // tab 显示时重算一次：此前若按过小的可视区布局过，这里兜底自愈
        UpdatePosterLayout();
        return _vm.LoadHomeCommand.ExecuteAsync(null);
    }

    /// <summary>「切换源」点击 → 数据源选择弹窗（参考影视仓），选择后切换首页数据源</summary>
    private async void OnSwitchSiteTapped(object? sender, TappedEventArgs e)
    {
        var sites = _vm.PlayableSites;
        if (sites.Count == 0)
        {
            _vm.HomeStatus = "暂无可用站点，请先在 设置 → 订阅源管理 添加订阅";
            return;
        }
        var dialog = new SitePickerDialogPage(sites, _vm.Site?.Key,
            site => MainThread.BeginInvokeOnMainThread(() => _ = _vm.SelectSiteCommand.ExecuteAsync(site)),
            _vm.FailedSites);
        await Shell.Current.Navigation.PushModalAsync(dialog);
    }

    /// <summary>分类 chip 点击 → 拉取该分类影片</summary>
    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as VisualElement)?.BindingContext is not VodCategory cat) return;
        await _vm.SelectCategoryAsync(cat);
    }

    /// <summary>选中分类 chip 用主题色实底 + 白字，其余用未激活 chip 色</summary>
    private void UpdateChipStyles()
    {
        var res = Application.Current?.Resources;
        var active = res?["PrimaryColor"] as Color ?? Colors.Purple;
        var inactive = res?["ChipInactiveColor"] as Color ?? Colors.Gray;
        var activeText = Colors.White;
        var inactiveText = res?["TextSecondaryColor"] as Color ?? Colors.Gray;

        foreach (var child in CategoryChipHost.Children)
        {
            if (child is not Border chip) continue;
            bool on = (chip.BindingContext as VodCategory)?.Id == _vm.SelectedCategoryId;
            chip.BackgroundColor = on ? active : inactive;
            if (chip.Content is Label label)
                label.TextColor = on ? activeText : inactiveText;
        }
    }

    /// <summary>海报卡点击 → 观看页（带源定位参数；type 必传——猫爪源等无 api 特征的源靠它路由）</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.Site is null) return;
        if ((sender as VisualElement)?.BindingContext is not VodItem item) return;

        var query = $"watch?title={Uri.EscapeDataString(item.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(item.SourceKey)}" +
                    $"&type={_vm.Site.Type}" +
                    $"&api={Uri.EscapeDataString(_vm.Site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(item.Id)}" +
                    $"&year={Uri.EscapeDataString(item.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(item.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}" +
                    // 封面透传：观看页不再拉详情，播放历史的海报靠它
                    $"&cover={Uri.EscapeDataString(item.Cover ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
