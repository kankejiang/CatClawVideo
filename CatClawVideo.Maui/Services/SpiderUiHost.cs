using System.Text.Json.Nodes;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 桥爬虫 UI 事件 → MAUI 原生交互：把 Java 桥上行的
/// <c>ui-dialog</c>（标题/消息/列表/按钮/二维码矩阵）、<c>ui-dismiss</c>、<c>ui-toast</c>
/// 渲染为桌面原生对话框，用户操作经 <see cref="JavaSpiderRuntime.SendUiResultAsync"/> 回传桥侧。
/// 对齐 TVBox 交互：点「登入自己网盘」弹「已登录+启用中」列表 → 点网盘弹扫码二维码。
/// </summary>
public static class SpiderUiHost
{
    private static readonly Dictionary<int, Page> Windows = new();
    /// <summary>QEMU Guard VM 弹出的对话框 seq（用户操作经 GuardRuntime 回传 guest，而非桥 stdin）。</summary>
    private static readonly HashSet<int> QemuSeqs = new();
    private static CatClawVideo.Core.Providers.JavaSpiderRuntime? _rt;

    /// <summary>桌面 JVM 桥运行时（未 Attach 时为 null——Android/无桥平台）。</summary>
    public static CatClawVideo.Core.Providers.JavaSpiderRuntime? DesktopJar => _rt;

    public static void Attach(CatClawVideo.Core.Providers.JavaSpiderRuntime rt)
    {
        _rt = rt;
        rt.UiEvent = ev => MainThread.BeginInvokeOnMainThread(() => _ = HandleAsync(ev));
        // Guard VM（QEMU）的 UI 事件走同一条渲染管线（src=qemu 已标注，用户操作路由回 guest）
        Core.Services.QemuThunder.GuardRuntime.UiEvent += ev =>
            MainThread.BeginInvokeOnMainThread(() => _ = HandleAsync(ev));
    }

    private static async Task HandleAsync(JsonObject ev)
    {
        try
        {
            switch (ev["ev"]?.GetValue<string>())
            {
                case "ui-dialog":
                    if (ev["src"]?.GetValue<string>() == "qemu")
                        QemuSeqs.Add(ev["seq"]?.GetValue<int>() ?? 0);
                    _dialogSeen?.TrySetResult(true);   // 等待方（网盘入口）确认 jar 已弹窗
                    await ShowDialogAsync(ev).ConfigureAwait(true);
                    break;
                case "ui-dismiss":
                    Close(ev["seq"]?.GetValue<int>() ?? 0);
                    break;
                case "ui-toast":
                    var text = ev["text"]?.GetValue<string>() ?? "";
                    DiagLog.Write($"[spider-ui] toast: {text}");
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[spider-ui] 事件处理失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>用户操作回传：桥侧对话框走 stdin，QEMU Guard VM 对话框走控制口 UIR。</summary>
    private static Task SendUiResultAsync(int seq, int which)
    {
        if (QemuSeqs.Contains(seq))
        {
            Core.Services.QemuThunder.GuardRuntime.Engine?.SendUiResult(seq, which);
            QemuSeqs.Remove(seq);
            return Task.CompletedTask;
        }
        return _rt?.SendUiResultAsync(seq, which) ?? Task.CompletedTask;
    }

    private static async Task ShowDialogAsync(JsonObject ev)
    {
        var seq = ev["seq"]?.GetValue<int>() ?? 0;
        var title = ev["title"]?.GetValue<string>() ?? "";
        var message = ev["message"]?.GetValue<string>() ?? "";

        // 二维码对话框（扫码登录）
        if (ev["qr"] is JsonObject qr)
        {
            await ShowQrAsync(seq, title, qr).ConfigureAwait(true);
            return;
        }

        // 列表选择（如「我的夸父- 未登录 / 停用中」）——ActionSheet 单层最贴 Android setItems
        if (ev["items"] is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
        {
            var list = arr.Select(x => x?.GetValue<string>() ?? "").ToArray();
            var pick = await Shell.Current.DisplayActionSheet(
                string.IsNullOrWhiteSpace(title) ? message : title, "取消", null, list).ConfigureAwait(true);
            var idx = pick is null ? -2 : Array.IndexOf(list, pick);
            await SendUiResultAsync(seq, idx < 0 ? -2 : idx).ConfigureAwait(true);
            return;
        }

        // 按钮组合
        var pos = ev["positive"]?.GetValue<string>();
        var neg = ev["negative"]?.GetValue<string>();
        if (pos is not null && neg is not null)
        {
            var ok = await Shell.Current.DisplayAlertAsync(title, message, pos, neg).ConfigureAwait(true);
            await SendUiResultAsync(seq, ok ? -1 : -2).ConfigureAwait(true);
        }
        else
        {
            await Shell.Current.DisplayAlertAsync(title, message, pos ?? "确定").ConfigureAwait(true);
            await SendUiResultAsync(seq, -1).ConfigureAwait(true);
        }
    }

    /// <summary>二维码弹窗：黑白矩阵 → 手写 BMP（零依赖）→ Image；整页模态展示，手机扫码登录。</summary>
    private static async Task ShowQrAsync(int seq, string title, JsonObject qr)
    {
        var w = qr["w"]?.GetValue<int>() ?? 0;
        var h = qr["h"]?.GetValue<int>() ?? 0;
        var pixels = (qr["pixels"] as System.Text.Json.Nodes.JsonArray)?
            .Select(x => x?.GetValue<int>() ?? 0).ToArray() ?? [];
        if (w <= 0 || h <= 0 || pixels.Length != w * h)
        {
            await SendUiResultAsync(seq, -2).ConfigureAwait(true);
            return;
        }

        var scale = Math.Max(4, 360 / Math.Max(w, h));
        var bmp = BuildBmp(w, h, pixels, scale);
        var page = new ContentPage
        {
            Title = string.IsNullOrWhiteSpace(title) ? "扫码登录" : title,
            BackgroundColor = Colors.Black,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                },
                Children =
                {
                    new Image
                    {
                        Source = ImageSource.FromStream(() => new MemoryStream(bmp)),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        Margin = 24,
                    },
                    new Button
                    {
                        Text = "取消",
                        TextColor = Colors.White,
                        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#333333"),
                        Margin = new Thickness(24, 0, 24, 24),
                        Command = new Microsoft.Maui.Controls.Command(async () =>
                        {
                            try { await Shell.Current.Navigation.PopModalAsync(); } catch { }
                        }),
                    },
                },
            },
        };
        Windows[seq] = page;
        await Shell.Current.Navigation.PushModalAsync(page).ConfigureAwait(true);
    }

    private static void Close(int seq)
    {
        if (!Windows.Remove(seq, out _)) return;
        try { MainThread.BeginInvokeOnMainThread(() => _ = Shell.Current.Navigation.PopModalAsync()); } catch { }
    }

    // ═══════════ Guard 系网盘源「已登录+启用中」对话框（TVBox 同款交互） ═══════════

    /// <summary>ui-dialog 事件到达信号（jar 弹窗经桥上行时触发）。</summary>
    private static TaskCompletionSource<bool>? _dialogSeen;

    /// <summary>等待 jar 弹出对话框（指定时间内看有没有 ui-dialog 事件）。</summary>
    private static async Task<bool> WaitForDialogAsync(TimeSpan timeout)
    {
        _dialogSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = await Task.WhenAny(_dialogSeen.Task, Task.Delay(timeout)).ConfigureAwait(true);
        return done == _dialogSeen.Task && _dialogSeen.Task.Result;
    }

    /// <summary>
    /// 网盘配置入口的完整链路（首页点「登入自己网盘」等卡片直接走这里，不进播放页）：
    /// 调 GetPlaySourcesAsync —— detailContent 失败容错里会执行 playerContent + danmaku 钩子
    /// → 壳框架 proxyInvoke → Guard VM 里的 so 弹「已登录+启用中」对话框/扫码二维码（QEMU
    /// 原生行为，与 TVBox 真机一致）→ ui-dialog 事件上行 → <see cref="HandleAsync"/> 自动渲染。
    ///
    /// <para>2026-09-24 用户拍板：宿主侧登录适配（官网登录 WebView / 硬编码网盘列表）全部删除——
    /// jar 框架全权负责登录 UX，宿主只做 UI 接入。没等到弹窗时仅保留 jar 自带的 do=config
    /// Cookie 推送页兜底（那是 jar 自己的页面，非宿主适配）。</para>
    /// </summary>
    public static async Task OpenDriveEntryAsync(VodSiteInfo site, VodItem item)
    {
        try
        {
            var provider = Application.Current?.Handler?.MauiContext?.Services
                .GetService<Core.Interfaces.IVodSourceProvider>();
            if (provider is null) return;

            // 解析链（playerContent + danmaku 钩子）完成后 jar 若弹窗，事件即时渲染；
            // 只留 800ms 跨进程到达缓冲——不再白等固定窗口
            var wait = WaitForDialogAsync(TimeSpan.FromMilliseconds(800));
            await provider.GetPlaySourcesAsync(site, item).ConfigureAwait(true);
            if (await wait.ConfigureAwait(true)) return;   // jar 已弹对话框/二维码（宿主已展示）

            // jar 没弹窗（QEMU so 未就绪等）→ 兜底：Cookie 推送页（jar 自己的 do=config HTML）
            var url = $"http://127.0.0.1:{Core.Services.SpiderProxyServer.ActivePort}/proxy?do=config&url={Uri.EscapeDataString(item.Id)}";
            var title = Uri.EscapeDataString(item.Title ?? "网盘配置");
            await Shell.Current.GoToAsync($"webpage?title={title}&url={Uri.EscapeDataString(url)}").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[spider-ui] 网盘配置入口异常: {ex.Message}");
        }
    }


    /// <summary>生成 24 位 BMP（黑白放大；pixels 1=黑 0=白）。BMP 底行优先。</summary>
    private static byte[] BuildBmp(int w, int h, int[] px, int scale)
    {
        var ow = w * scale;
        var oh = h * scale;
        var rowSize = (ow * 3 + 3) / 4 * 4;
        var dataSize = rowSize * oh;
        var b = new byte[54 + dataSize];

        b[0] = (byte)'B';
        b[1] = (byte)'M';
        WriteInt(b, 2, 54 + dataSize);
        WriteInt(b, 10, 54);
        WriteInt(b, 14, 40);
        WriteInt(b, 18, ow);
        WriteInt(b, 22, oh);
        b[26] = 1;
        b[28] = 24;
        WriteInt(b, 34, dataSize);

        for (var y = 0; y < oh; y++)
        {
            var srcY = h - 1 - (y / scale);   // BMP 自底向上
            var rowStart = 54 + y * rowSize;
            for (var x = 0; x < ow; x++)
            {
                var c = px[srcY * w + (x / scale)] == 1 ? (byte)0 : (byte)255;
                var o = rowStart + x * 3;
                b[o] = c;
                b[o + 1] = c;
                b[o + 2] = c;
            }
        }
        return b;
    }

    private static void WriteInt(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }
}
