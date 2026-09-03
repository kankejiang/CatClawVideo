using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>首页 ViewModel：URL 快速播放入口（订阅源功能上线前的播放测试与占位）。</summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>公开测试流（Apple HLS 示例，验证 m3u8 播放链路）</summary>
    public const string TestStreamUrl =
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";

    [ObservableProperty]
    private string _urlInput = string.Empty;

    /// <summary>URL 非空校验（用于按钮可用态）</summary>
    [ObservableProperty]
    private bool _canPlay;

    partial void OnUrlInputChanged(string value) => CanPlay = !string.IsNullOrWhiteSpace(value);

    [RelayCommand]
    private async Task PlayAsync()
    {
        var url = UrlInput?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        await NavigateToPlayerAsync(url, "网页播放");
    }

    [RelayCommand]
    private Task PlayTestStreamAsync() =>
        NavigateToPlayerAsync(TestStreamUrl, "HLS 测试流");

    private static async Task NavigateToPlayerAsync(string url, string title)
    {
        await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(url)}");
    }
}
