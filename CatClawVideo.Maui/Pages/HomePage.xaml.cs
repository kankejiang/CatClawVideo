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

        // 海报墙布局：与历史/收藏页一致，统一走 PosterLayoutHelper（固定卡片尺寸，列数自适应）
        PosterGrid.SizeChanged += (_, _) => ApplyPosterLayout();

        // 空源引导面板：列表变化时刷新可见性
        _vm.Items.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePairPanel);
    }

    public Task OnTabShownAsync()
    {
        ApplyPosterLayout();
        UpdatePairPanel();
        return _vm.LoadHomeCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 没有可用源时显示本机地址 + 配对二维码。
    /// 二维码内容 <c>catclaw://pair?u=http://&lt;本机IP&gt;:&lt;端口&gt;&amp;n=&lt;设备名&gt;</c>，
    /// 手机扫码后 POST 到本机的 LinkServer 完成配对（写入解析节点地址）。
    /// </summary>
    private void UpdatePairPanel()
    {
        try
        {
            bool noSource = _vm.PlayableSites.Count == 0;
            PairPanel.IsVisible = noSource;
            if (!noSource) return;

            var ip = CatClawVideo.Core.Services.LanInfo.PrimaryIPv4();
            var port = CatClawVideo.Core.Services.LinkServer.DefaultPort;
            var payload = Services.PairQr.Payload(ip, port);

            if (PairQrImage.Source is null)
            {
                var png = Services.PairQr.Png(payload);
                PairQrImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
            }
            PairAddressLabel.Text = $"本机地址：{ip}:{port}";
        }
        catch (Exception ex)
        {
            PairAddressLabel.Text = $"二维码生成失败：{ex.Message}";
        }
    }

    /// <summary>海报墙布局：与历史/收藏页同一套 PosterLayoutHelper（Android 卡高 182 / Windows 252，宽 2:3，列数自适应）</summary>
    private void ApplyPosterLayout()
    {
#if WINDOWS
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height, cap: 252);
#else
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height);
#endif
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
