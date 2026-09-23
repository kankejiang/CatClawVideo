namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 网页展示页：全屏 WebView 渲染宿主本地 proxy 提供的 HTML 交互页
/// （Guard 系网盘源的「云盘配置」：登入自己网盘 / 启停网盘 / 清除 Cookie 等，
/// 对齐 TVBox「嗅探后渲染网页」的行为——那类卡片不是视频，绝不能进播放器）。
/// 配置完成点顶栏返回即回到观看页。
/// </summary>
public partial class WebPagePage : ContentPage, IQueryAttributable
{
    private string _url = string.Empty;

    public WebPagePage()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var titleObj) && titleObj is string title && title.Length > 0)
            TitleLabel.Text = title;
        if (query.TryGetValue("url", out var urlObj) && urlObj is string url)
            _url = url;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_url.Length > 0)
            Web.Source = _url;
    }

    private void OnBackTapped(object? sender, EventArgs e)
        => Shell.Current.GoToAsync("..");
}
