using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 搜索站点勾选弹窗（对位 TVBox <c>SearchCheckboxDialog</c> + <c>SOURCES_FOR_SEARCH</c>）：
/// 逐源勾选、按订阅记忆，只有勾上的站点进本次跨源搜索。
///
/// <para><b>为什么不复用单选的那个弹窗</b>：<see cref="SitePickerDialogPage"/> 是「换首页数据源」，
/// 一次只能站一个台；搜索正相反，是要**同时**问多个台，套单选会把它做成一个开关。</para>
///
/// <para><b>遥控器</b>：↑↓ 在站点间走、到底再 ↓ 落到「确定」，回车切换该行（在「确定」上则提交关闭），
/// ← → 一键全选 / 全不选，Back 放弃本次改动（不写盘）。
/// 与 <c>SitePickerDialogPage</c> 同一条教训：模态盖在下面那层页之上时按键必须**全量吃掉**，
/// 否则「遥控器没反应」的真相是按键一直在操作背后的页面。</para>
/// </summary>
public partial class SearchSourceDialogPage : ContentPage, IRemoteKeyHandler
{
    private readonly IReadOnlyList<VodSiteInfo> _sites;
    private readonly List<bool> _on = [];
    private readonly List<Border> _rows = [];
    private readonly string? _subscription;
    private readonly Color _divider;

    private Border _okBtn = null!;
    private Label _okLabel = null!;
    private ScrollView _scroll = null!;
    private int _index;
    private bool _onOk;

    public SearchSourceDialogPage(IReadOnlyList<VodSiteInfo> sites)
    {
        _sites = sites;
        _subscription = sites.Count > 0 ? sites[0].SubscriptionName : null;
        foreach (var s in sites) _on.Add(SearchSourceStore.IsSearchable(s.SubscriptionName, s.Key));

        BackgroundColor = Color.FromArgb("#B3000000");
        var res = Application.Current!.Resources;
        var cardBg = (Color)res["CardBackgroundColor"];
        _divider = (Color)res["DividerColor"];
        var textMain = (Color)res["TextPrimaryColor"];
        var textSub = (Color)res["TextSecondaryColor"];

        var stack = new VerticalStackLayout { Spacing = 10 };

        var titleRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(36)) } };
        titleRow.Add(new Label
        {
            Text = "选择搜索站点",
            FontSize = 16,
            FontFamily = "OpenSansSemibold",
            TextColor = textMain,
            VerticalOptions = LayoutOptions.Center,
        }, 0, 0);
        titleRow.Add(MakeAction("✕", textSub, CloseAsync), 1, 0);
        stack.Add(titleRow);

        stack.Add(new Label
        {
            Text = "只搜勾上的站点（按订阅分别记）；← → 一键全选 / 全不选。",
            FontSize = 11.5,
            TextColor = textSub,
        });

        var listStack = new VerticalStackLayout { Spacing = 6 };
        for (var i = 0; i < _sites.Count; i++)
        {
            var captured = i;
            var row = new Border
            {
                Padding = new Thickness(12, 9),
                StrokeThickness = 1,
                Stroke = _divider,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = cardBg,
                Content = new Label
                {
                    FontSize = 13,
                    TextColor = textMain,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1,
                },
            };
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Toggle(captured)) });
            listStack.Add(row);
            _rows.Add(row);
        }
        _scroll = new ScrollView { Content = listStack, MaximumHeightRequest = 420 };
        stack.Add(_scroll);

        _okLabel = new Label
        {
            FontSize = 13.5,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
        };
        _okBtn = new Border
        {
            Padding = new Thickness(16, 9),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            BackgroundColor = (Color)res["PrimaryButtonBackgroundColor"],
            Content = _okLabel,
        };
        _okBtn.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Commit) });
        stack.Add(_okBtn);

        var card = new Border
        {
            WidthRequest = 520,
            MaximumHeightRequest = 640,
            BackgroundColor = cardBg,
            Stroke = _divider,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 20,
            Content = stack,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };

        // 遮罩单独一层：点卡片外的空白也能关（和 SitePickerDialogPage 一致的退出路径，别只留一条）
        var backdrop = new Border { BackgroundColor = Colors.Transparent };
        backdrop.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CloseAsync()) });

        Content = new Grid { Children = { backdrop, card } };
        RefreshRows();
    }

    private static Border MakeAction(string text, Color color, Func<Task> onClick)
    {
        var b = new Border
        {
            Padding = new Thickness(8, 4),
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Label { Text = text, FontSize = 15, TextColor = color, HorizontalOptions = LayoutOptions.Center },
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };
        b.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await onClick()) });
        return b;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
        }
        catch (Exception ex) { Maui.Services.BtFileLog.Write($"[搜索源弹窗] 关闭失败：{ex.Message}"); }
    }

    private void Toggle(int i)
    {
        if (i < 0 || i >= _on.Count) return;
        _on[i] = !_on[i];
        _index = i;
        _onOk = false;
        RefreshRows();
    }

    private void ToggleAll(bool on)
    {
        for (var i = 0; i < _on.Count; i++) _on[i] = on;
        RefreshRows();
    }

    private void RefreshRows()
    {
        var white = Color.FromArgb("#FFFFFF");
        for (var i = 0; i < _rows.Count; i++)
        {
            var site = _sites[i];
            if (_rows[i].Content is Label lbl)
                lbl.Text = (_on[i] ? "☑ " : "☐ ") + site.Name
                    + (string.IsNullOrEmpty(site.SubscriptionName) ? "" : $"　· {site.SubscriptionName}");
            _rows[i].Stroke = !_onOk && i == _index ? white : _divider;
        }
        _okBtn.Stroke = _onOk ? white : Colors.Transparent;
        _okBtn.StrokeThickness = _onOk ? 2 : 0;
        _okLabel.Text = $"确定（搜 {_on.Count(x => x)} / {_on.Count} 个源）";
    }

    private void Commit()
    {
        SearchSourceStore.SetAll(_subscription, _sites.Select((s, i) => (s.Key, _on[i])));
        _ = CloseAsync();
    }

    // ═══════════════════ 遥控器 ═══════════════════

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RemoteKeyRouter.Push(this);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        RemoteKeyRouter.Pop(this);
    }

    public void FocusContent() { }

    public void BlurContent() { }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Up:
                if (_onOk) _onOk = false;
                else _index = Math.Max(0, _index - 1);
                break;

            case RemoteKey.Down:
                if (!_onOk && _index >= _rows.Count - 1) _onOk = true;
                else if (!_onOk) _index++;
                break;

            case RemoteKey.Left:
                ToggleAll(true);
                return true;

            case RemoteKey.Right:
                ToggleAll(false);
                return true;

            case RemoteKey.Enter:
                if (_onOk) Commit();
                else Toggle(_index);
                return true;

            case RemoteKey.Back:
                _ = CloseAsync();
                return true;

            default:
                return false;
        }

        if (_onOk && _rows.Count > 0)
            _ = _scroll.ScrollToAsync(_rows[^1], ScrollToPosition.End, false);
        else if (_index >= 0 && _index < _rows.Count)
            _ = _scroll.ScrollToAsync(_rows[_index], ScrollToPosition.MakeVisible, false);
        RefreshRows();
        return true;
    }
}
