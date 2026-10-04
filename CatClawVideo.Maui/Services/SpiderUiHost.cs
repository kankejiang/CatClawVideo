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
        CatClaw.Qemu.GuardRuntime.UiEvent += ev =>
            MainThread.BeginInvokeOnMainThread(() => _ = HandleAsync(ev));
    }

    private static async Task HandleAsync(JsonObject ev)
    {
        try
        {
            // 原文留痕：「弹了但没东西」这类问题只能看 jar 到底上行了什么规格
            // （qr.pixels 会很长，故截断）
            var raw = RedactUrls(ev.ToJsonString());
            // token 不落盘：登录 URL 的会话 token 在 query 里。2026-10-01 op=qrtext 自检实测：
            // 只 mask `qrText` 字段不够——同一段 URL 也会作为 jar 的可见文本落进 message，
            // 所以整份留痕（日志行与 spider-ui-last.json）统一按 URL 掩掉。
            DiagLog.Write($"[spider-ui] ⬆ {raw.Length}B {(raw.Length > 900 ? raw[..900] + "…" : raw)}");
            // 全量另存一份：日志里那行截断到 900 字符，比对「点了之后框有没有真的变」要看全文
            try { File.WriteAllText(Core.AppPaths.Of("spider-ui-last.json"), raw); } catch { }

            switch (ev["ev"]?.GetValue<string>())
            {
                case "ui-dialog":
                    if (ev["src"]?.GetValue<string>() == "qemu")
                        QemuSeqs.Add(ev["seq"]?.GetValue<int>() ?? 0);
                    _dialogSeen?.TrySetResult(true);   // 等待方（网盘入口）确认 jar 已弹窗
                    await ShowDialogAsync(ev).ConfigureAwait(true);
                    break;
                case "ui-qr":
                    // 桥先弹了「还没有码」的框（壳异步取回登录 URL 才把码画出来），码到了按 seq
                    // 补发一条：直接把界面升级成扫码整页（ShowQrAsync 连「取消」的路由都挂在这个 seq 上）。
                    // 触发点在桩侧 View.invalidate() → Dialog 刷新线程（2026-10-01 扫码链路）。
                    _dialogSeen?.TrySetResult(true);
                    // 补发的可能是像素码（jar 自己建的位图），也可能是 qrText（只给链接的源）——
                    // 后者宿主出码，同一条 seq 升级成扫码页。
                    if (ev["qr"] is JsonObject qrLate)
                        await ShowQrAsync(ev["seq"]?.GetValue<int>() ?? 0,
                            ev["title"]?.GetValue<string>() ?? "扫码登录", qrLate).ConfigureAwait(true);
                    else if (HostQrFromText(ev["qrText"]?.GetValue<string>()) is JsonObject qrFromText)
                        await ShowQrAsync(ev["seq"]?.GetValue<int>() ?? 0,
                            ev["title"]?.GetValue<string>() ?? "扫码登录", qrFromText).ConfigureAwait(true);
                    break;
                case "ui-dismiss":
                    Close(ev["seq"]?.GetValue<int>() ?? 0);
                    break;
                case "ui-rows":
                    // jar 就地改了自定义视图的按钮文字（切「启用中/停用中」），刷新已开着的框
                    if (Windows.TryGetValue(ev["seq"]?.GetValue<int>() ?? -1, out var host)
                        && host is Pages.SpiderDialogPage sdp && ev["rows"] is JsonArray ra)
                        MainThread.BeginInvokeOnMainThread(() => sdp.UpdateRows(ParseRows(ra)));
                    // A 路线：树渲染页整树换新（jar 改的不只是可点行，还有状态文本）
                    else if (Windows.TryGetValue(ev["seq"]?.GetValue<int>() ?? -1, out var host2)
                        && host2 is Pages.SpiderTreeDialogPage tp2 && ev["tree"] is JsonObject tr2)
                        MainThread.BeginInvokeOnMainThread(() => tp2.UpdateTree(tr2.ToJsonString()));
                    break;
                case "ui-toast":
                    var text = ev["text"]?.GetValue<string>() ?? "";
                    DiagLog.Write($"[spider-ui] toast: {text}");
                    if (text.Length > 0) await ShowToastAsync(text).ConfigureAwait(true);
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
            CatClaw.Qemu.GuardRuntime.Engine?.SendUiResult(seq, which);
            QemuSeqs.Remove(seq);
            return Task.CompletedTask;
        }
        return _rt?.SendUiResultAsync(seq, which) ?? Task.CompletedTask;
    }

    /// <summary>
    /// 宿主自己发起的对话框/扫码页用的 seq：负数递降，永不与桥的 seq（从 1 起）相撞。
    /// 用户点「取消」时会把这个负 seq 回传桥，桥侧按“无此待决”丢弃即可。
    /// </summary>
    private static int _localSeq = -1000;
    private static int NextLocalSeq() => System.Threading.Interlocked.Decrement(ref _localSeq);

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

        // ★ 通用通道（对齐 TVBox：爬虫给 URL、宿主出码）：桥只需带 `qrText`，
        //   图由 QrPng 生成——不需要任何 Android 绘制仿真，任何源共用一条路。
        if (HostQrFromText(ev["qrText"]?.GetValue<string>()) is JsonObject qrFromText)
        {
            await ShowQrAsync(seq, string.IsNullOrWhiteSpace(title) ? "扫码登录" : title, qrFromText)
                .ConfigureAwait(true);
            return;
        }

        // A 路线（2026-10-02）：桥上行了整棵 View 树（tree 字段）→ 树渲染页按 jar 的
        // 文本/字号/颜色/背景/结构 1:1 还原（对齐真机观感）。rows 只作降级兜底。
        // 底部按钮（positive/negative/neutral）不在树里 —— AlertDialog 的 Builder 按钮，
        // 一并传给页面画出来（真机「禁用(左)/取消(右)」就是它们）。
        if (ev["tree"] is JsonObject tree)
        {
            var tSeq = ev["seq"]?.GetValue<int>() ?? 0;
            string Pos() => ev["positive"]?.GetValue<string>() ?? "";
            string Neg() => ev["negative"]?.GetValue<string>() ?? "";
            string Neu() => ev["neutral"]?.GetValue<string>() ?? "";
            var tp = new Pages.SpiderTreeDialogPage(tSeq, tree.ToJsonString(),
                idx => _ = SendUiResultAsync(tSeq, idx),
                () => _ = SendUiResultAsync(tSeq, -2),   // ✕/遮罩/Back = 取消
                Pos(), Neg(), Neu());
            Windows[tSeq] = tp;
            await Shell.Current.Navigation.PushModalAsync(tp).ConfigureAwait(true);
            return;
        }

        // 行 / 格结构（桥摊平的自定义 View 树而来）→ 两栏对话框：
        // 网盘行是「盘名占宽 + 启用/停用占窄」，用 ActionSheet 平铺会排成 8 行、和真机差很远
        if (ev["rows"] is JsonArray { Count: > 0 } rowArr)
        {
            var rows = ParseRows(rowArr);
            if (rows.Count > 0)
            {
                var dlg = new Pages.SpiderDialogPage(title, message, rows,
                    idx => _ = SendUiResultAsync(seq, idx),
                    () => _ = SendUiResultAsync(seq, -2));   // ✕/遮罩/Back = Android BUTTON_NEGATIVE（取消）
                Windows[seq] = dlg;
                await Shell.Current.Navigation.PushModalAsync(dlg).ConfigureAwait(true);
                return;
            }
        }

        // 列表选择（AlertDialog.setItems）：合成树页渲染（每项 = 可点行，点击回传下标），
        // 不再用 MAUI ActionSheet（2026-10-02 用户反馈统一视觉）。点项后 jar 自己 dismiss。
        if (ev["items"] is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
        {
            var rowsJson = new System.Text.Json.Nodes.JsonArray();
            var n = 0;
            foreach (var x in arr)
                rowsJson.Add(new JsonObject
                {
                    ["k"] = "LinearLayout", ["i"] = n++,
                    ["p"] = new JsonArray(14, 13, 14, 13),
                    ["c"] = new JsonArray(new JsonObject
                    {
                        ["k"] = "TextView", ["t"] = x?.GetValue<string>() ?? "",
                        ["ts"] = 14, ["tc"] = -16777216,
                    }),
                });
            var synth = new JsonObject
            {
                ["k"] = "LinearLayout", ["o"] = 1,
                ["p"] = new JsonArray(8, 8, 8, 8),
                ["bg"] = new JsonObject { ["c"] = -1, ["r"] = 14.0 },
                ["c"] = rowsJson,
            };
            var tp = new Pages.SpiderTreeDialogPage(seq, synth.ToJsonString(),
                idx => _ = SendUiResultAsync(seq, idx),
                () => _ = SendUiResultAsync(seq, -2),
                negative: "取消");
            Windows[seq] = tp;
            await Shell.Current.Navigation.PushModalAsync(tp).ConfigureAwait(true);
            return;
        }

        // 按钮组合（无自定义 View 的纯 AlertDialog）：合成一棵最小树走树渲染页（白卡片 +
        // 底部按钮），视觉与其余对话框统一，不再用 MAUI DisplayAlert（2026-10-02 用户反馈）。
        var pos = ev["positive"]?.GetValue<string>();
        var neg = ev["negative"]?.GetValue<string>();
        var neu = ev["neutral"]?.GetValue<string>();
        {
            JsonObject TV(string t, double ts, int tc) => new()
            {
                ["k"] = "TextView", ["t"] = t, ["ts"] = ts, ["tc"] = tc,
            };
            var synth = new JsonObject
            {
                ["k"] = "LinearLayout", ["o"] = 1,
                ["p"] = new JsonArray(24, 20, 24, 20),
                ["bg"] = new JsonObject { ["c"] = -1, ["r"] = 14.0 },
                ["c"] = new JsonArray(
                    string.IsNullOrEmpty(title) ? null : TV(title!, 17, -16777216),
                    string.IsNullOrEmpty(message) ? null : TV(message!, 13.5, -8355712)),
            };
            var tp = new Pages.SpiderTreeDialogPage(seq, synth.ToJsonString(),
                idx => _ = SendUiResultAsync(seq, idx),
                () => _ = SendUiResultAsync(seq, -2),
                pos ?? (string.IsNullOrEmpty(neu) && string.IsNullOrEmpty(neg) ? "确定" : ""),
                neg ?? "", neu ?? "");
            Windows[seq] = tp;
            await Shell.Current.Navigation.PushModalAsync(tp).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 二维码弹窗：整页模态展示，手机扫码登录。
    /// <para>图优先用桥侧 <c>ImageIO</c> 出的 PNG —— 这里手搓的 24 位 BMP 在 WinUI 上解不出来,
    /// 整页只剩一个「取消」(2026-09-24 实测)。QEMU guest 只发 pixels,故保留 BMP 兜底。</para>
    /// </summary>
    private static async Task ShowQrAsync(int seq, string title, JsonObject qr)
    {
        var w = qr["w"]?.GetValue<int>() ?? 0;
        var h = qr["h"]?.GetValue<int>() ?? 0;
        var pngB64 = qr["png"]?.GetValue<string>();
        byte[]? png = null;
        if (!string.IsNullOrEmpty(pngB64))
        {
            try { png = Convert.FromBase64String(pngB64!); } catch { png = null; }
        }
        var pixels = (qr["pixels"] as System.Text.Json.Nodes.JsonArray)?
            .Select(x => x?.GetValue<int>() ?? 0).ToArray() ?? [];
        if (png is null && (w <= 0 || h <= 0 || pixels.Length != w * h))
        {
            await SendUiResultAsync(seq, -2).ConfigureAwait(true);
            return;
        }

        var img = png ?? BuildBmp(w, h, pixels, Math.Max(4, 360 / Math.Max(w, h)));
        var image = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(img)),
            BackgroundColor = Colors.White,     // 二维码外留白：黑底上贴黑码扫不出来
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = 24,
        };
        // 宿主把码**解回 URL**（与源无关）：日志里能看到"这张码到底是什么"，界面上给一行可复制文本
        // ——手机不在手边时可以直接在电脑浏览器里打开完成授权。token 不外泄：日志只记长度与 host。
        var loginUrl = Core.Services.QrDecode.FromPng(img);
        Label? urlLabel = string.IsNullOrEmpty(loginUrl) ? null : new Label
        {
            Text = loginUrl,
            FontSize = 11,
            TextColor = Colors.Gray,
            Margin = new Thickness(24, 0, 24, 8),
            HorizontalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        if (urlLabel is not null)
            // HostOf 自带 try：像素路径解出的内容未经 FirstUrl 正则筛选，万一是非 URL 文本，
            // 不能让 UriFormatException 把这个 seq 的扫码页整条打断（表现成静默无框）
            DiagLog.Write($"[spider-ui] 宿主解码扫码 URL 成功：{loginUrl!.Length} 字符，host={HostOf(loginUrl!)}" +
                          "（日志不记 token）");
        else
            DiagLog.Write("[spider-ui] 宿主未能从二维码解出 URL（不影响显示，仅少一行可复制文本）");

        // 卡片内容：二维码 + 可复制的 URL（关闭按钮在下面统一加，避免出现两个取消）
        // 行必须显式排：不写的话 Image 与 urlLabel 都落 row 0，挤在一起
        var stack = new VerticalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
            Children = { image },
        };
        if (urlLabel is not null) stack.Children.Add(urlLabel);
        var grid = stack;
        // ★ 不再整页模态（2026-10-03，用户反馈「扫码弹窗挡住二维码」）：
        //   旧实现 PushModalAsync 一个黑底整页，把 jar 自己弹的网盘面板（已登录+启用中/停用中 那些行）
        //   完全盖住，用户既看不到状态、也点不到下面的按钮。
        //   改为**半透明浮层**挂在当前页上：卡片居中、四周仍可见并可点；点浮层空白处或「关闭」收起。
        //   这样 jar 的面板保持可见，扫码与操作可以对照着来。
        var card = new Border
        {
            BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#F2101010"),
            Stroke = Microsoft.Maui.Graphics.Color.FromArgb("#40FFFFFF"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Padding = new Thickness(10, 14, 10, 8),
            WidthRequest = 360,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Content = grid,
        };

        var dim = new Grid
        {
            BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#99000000"),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // 标题
                new RowDefinition(GridLength.Star),    // 卡片
            },
        };
        dim.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "扫码登录" : title,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(16, 18, 16, 10),
        }, 0, 0);
        dim.Add(card, 0, 1);

        // 浮层：挂到当前页的根 Grid（**不进导航栈** ⇒ 不挡 jar 面板的交互与可见性）。
        var page = new ContentPage
        {
            BackgroundColor = Colors.Transparent,
            Content = dim,
        };

        void EnsureMounted()
        {
            if (page.Parent is not null) return;
            if (Shell.Current.CurrentPage is ContentPage host && host.Content is Grid root)
                root.Children.Add(page);
        }
        void DismissOverlay()
        {
            try { if (page.Parent is Grid g) g.Children.Remove(page); } catch { }
            if (ReferenceEquals(TargetOverlay, page)) TargetOverlay = null;
        }

        // 关闭按钮放在卡片内容末尾：告诉 jar「取消」（BUTTON_NEGATIVE），否则它一直挂着对话框
        var close = new Button
        {
            Text = "关闭",
            TextColor = Colors.White,
            BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#333333"),
            CornerRadius = 10,
            Margin = new Thickness(0, 12, 0, 0),
        };
        close.Command = new Microsoft.Maui.Controls.Command(async () =>
        {
            Windows.Remove(seq, out _);          // 先摘登记：jar 随后 ui-dismiss 走 Close 不会重复弹
            await SendUiResultAsync(seq, -2).ConfigureAwait(true);
            DismissOverlay();
        });
        grid.Children.Add(close);      // grid 现为 VerticalStackLayout（上面统一赋值）

        page.Loaded += (_, _) => EnsureMounted();
        EnsureMounted();
        TargetOverlay = page;          // 供 Close(seq) 摘除（浮层不在导航栈里）
        Windows[seq] = page;
    }

    /// <summary>URL 的 host（日志只到这一层——登录 URL 的 token 在 query 里，不能落盘）。</summary>
    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return "?"; }
    }

    private static readonly System.Text.RegularExpressions.Regex UrlRx =
        new(@"https?://[^\s""'<>\u005c]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>把留痕文本里的每个 URL 换成「长度+host」：登录 URL 与推送页 URL 的 query 都带会话 token。</summary>
    private static string RedactUrls(string json)
    {
        try
        {
            return UrlRx.Replace(json, m => $"<{m.Value.Length}B·host={HostOf(m.Value)}>");
        }
        catch { return json; }
    }

    /// <summary>
    /// 契约通道出码（源无关）：爬虫只给登录 URL 时，PNG 由宿主 <see cref="Core.Services.QrPng"/> 生成。
    /// 三个入口共用它——<c>ui-dialog.qrText</c>、异步补发的 <c>ui-qr.qrText</c>、<c>action()</c> 返回里带的链接。
    /// </summary>
    private static JsonObject? HostQrFromText(string? qrText)
    {
        if (string.IsNullOrWhiteSpace(qrText)) return null;
        // 桥侧不切边界（切错过一次，见 AlertDialog.firstUrl 的注释），可能连中文尾巴一起上行：
        // 用与 action 返回同一把尺子截出纯 URL，再出码。
        var url = Core.Services.QrPng.FirstUrl(qrText);
        if (string.IsNullOrEmpty(url))
        {
            DiagLog.Write($"[spider-ui] qrText {qrText!.Length} 字符里截不出 URL（宿主不出码）");
            return null;
        }
        var png = Core.Services.QrPng.FromText(url);
        if (png is not { Length: > 0 })
        {
            DiagLog.Write($"[spider-ui] qrText 出码失败（{url!.Length} 字符，编码异常）");
            return null;
        }
        DiagLog.Write($"[spider-ui] qrText {qrText!.Length} 字符 → 截出 URL {url!.Length} 字符 → " +
                      $"宿主出码 PNG {png!.Length}B");
        return new JsonObject { ["w"] = 0, ["h"] = 0, ["png"] = Convert.ToBase64String(png!) };
    }

    /// <summary>解析桥的 <c>rows</c>（每格 <c>{t:文本, i:条目下标}</c>）；首次渲染与后续刷新共用。</summary>
    private static List<IReadOnlyList<(string Text, int Index)>> ParseRows(JsonArray rowArr)
    {
        var rows = new List<IReadOnlyList<(string Text, int Index)>>();
        foreach (var r in rowArr)
        {
            if (r is not JsonArray cells) continue;
            var line = new List<(string Text, int Index)>();
            foreach (var c in cells)
            {
                if (c is not JsonObject co) continue;
                line.Add((co["t"]?.GetValue<string>() ?? "", co["i"]?.GetValue<int>() ?? 0));
            }
            if (line.Count > 0) rows.Add(line);
        }
        return rows;
    }

    /// <summary>从 action 的返回 JSON 里取给用户看的话（TVBox 的 actionResult 用 <c>msg</c> 字段）。</summary>
    private static string PickMsg(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.StartsWith('{')) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                ? m.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// toast 去重表：相同文案在 <see cref="ToastDedupeSeconds"/> 秒内只弹一次。
    /// 2026-10-03：guest 里的网盘爬虫失败会几秒内重试（实测「Go代理 1 进程仍在退出」连发 3 次），
    /// 每次都弹一条，用户观感就是「老弹窗」。日志照记，只压弹窗。
    /// </summary>
    private static readonly Dictionary<string, DateTime> _toastSeen = new(StringComparer.Ordinal);
    private const int ToastDedupeSeconds = 60;

    /// <summary>
    /// 非模态小提示：叠在当前页根 Grid 底部，淡入停留后自动消失。
    /// <para>根布局不是 Grid（没有可叠加的容器）时退回系统弹窗 —— 早先 toast 只写日志，
    /// 用户点「清除 Cookie」后什么反馈都看不到。</para>
    /// </summary>
    private static async Task ShowToastAsync(string text)
    {
        // 同一条文案短时间内只弹一次（guest 侧重试会连发；日志里仍逐条留痕）
        lock (_toastSeen)
        {
            var now = DateTime.UtcNow;
            if (_toastSeen.TryGetValue(text, out var last) && (now - last).TotalSeconds < ToastDedupeSeconds)
            {
                DiagLog.Write($"[spider-ui] toast 去重（{ToastDedupeSeconds}s 内重复）：{text}");
                return;
            }
            if (_toastSeen.Count > 64) _toastSeen.Clear();   // 文案种类有限，兜底防涨
            _toastSeen[text] = now;
        }
        // 先取 Shell 当前页：Shell 应用里 Window.Page 是 Shell 本身，拿不到可叠加的根布局
        var page = Shell.Current?.CurrentPage ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is not ContentPage cp || cp.Content is not Grid root)
        {
            try { if (page is not null) await page.DisplayAlertAsync("提示", text, "好的"); } catch { }
            return;
        }
        var tip = new Border
        {
            Content = new Label { Text = text, FontSize = 13.5, TextColor = Colors.White, Margin = new Thickness(14, 9) },
            BackgroundColor = Color.FromArgb("#E6323232"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 42),
            Opacity = 0,
        };
        root.Children.Add(tip);
        await tip.FadeTo(1, 120);
        await Task.Delay(1900);
        await tip.FadeTo(0, 260);
        root.Children.Remove(tip);
    }

    private static void Close(int seq)
    {
        if (!Windows.Remove(seq, out _)) return;
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 浮层（扫码页）不在导航栈里 → 直接从承载 Grid 摘掉；模态页才 PopModal。
                var target = (Microsoft.Maui.Controls.Element?)null;
                if (TargetOverlay is { } ov && ov.Parent is Grid og) { og.Children.Remove(ov); TargetOverlay = null; return; }
                _ = Shell.Current.Navigation.PopModalAsync();
            });
        }
        catch { }
    }

    /// <summary>当前挂着的扫码浮层（ShowQrAsync 挂载时登记，Close 时摘除）。</summary>
    private static ContentPage? TargetOverlay;

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
    /// jar 框架全权负责登录 UX，宿主只做 UI 接入。没等到弹窗时给一句人话提示
    /// （2026-10-03 用户要求删除「打开 jar 自带 do=config 网页」的兜底：它在 Android 上打不开，
    /// 在 Windows 上也不是好体验）。</para>
    /// </summary>
    public static async Task OpenDriveEntryAsync(VodSiteInfo site, VodItem item)
    {
        try
        {
            var provider = Application.Current?.Handler?.MauiContext?.Services
                .GetService<Core.Interfaces.IVodSourceProvider>();
            if (provider is null) return;

            // ① 卡片自带 action（TVBox doAction 语义）：网盘的「已登录+启用中」列表与扫码二维码
            //    只有爬虫的 action(String) 会弹出原生对话框 —— detailContent/playerContent
            //    那条路只会走到 Pan.proxyInput() 的贴 Cookie HTML 页（2026-09-24 实测确认）。
            if (item.Action.Length > 0 && provider is Core.Interfaces.IActionVodSourceProvider ap)
            {
                // 桩侧取码最多再等 1.5s（见 AlertDialog.huntQr 的重试），窗口要盖住这段
                var wAction = WaitForDialogAsync(TimeSpan.FromSeconds(5));
                var acted = await ap.DoActionAsync(site, item).ConfigureAwait(true);
                if (acted is not null)
                {
                    // 执行成功就到此为止：jar 自己弹框（登录列表/扫码）或只回一个 toast
                    // （「清除XX Cookie」）。往下掉会把用户丢进 jar 的贴 Cookie 推送页
                    // ——2026-09-24 用户实测「点清除却进推送页」正是这么来的。
                    if (await wAction.ConfigureAwait(true)) return;
                    var msg = PickMsg(acted);
                    // ★ 通用登录接线（对齐 TVBox 契约：爬虫给串、宿主出码）：action 的返回里
                    //   只要有 http(s) 链接（msg/content/url 任一处），就直接把它渲染成二维码整页，
                    //   不再依赖 jar 的 Android 对话框——这条对**任何**照契约回 URL 的源都成立。
                    var loginUrl = Core.Services.QrPng.FirstUrl(acted);
                    if (HostQrFromText(loginUrl) is JsonObject actionQr)
                    {
                        var seq = NextLocalSeq();
                        DiagLog.Write($"[spider-ui] action 返回登录 URL → 宿主自己出码 " +
                            $"{loginUrl!.Length} 字符（不依赖 jar 绘制）");
                        await ShowQrAsync(seq, site.Name, actionQr).ConfigureAwait(true);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(msg)) { await ShowToastAsync(msg!); return; }
                    // 但「既没弹窗、又没回话」不能静默 return：2026-09-30 实测用户连点三次
                    // 「扫码登录」毫无反应，日志里 `FishConfig.action(quark_scan) → `（空），
                    // 而桥侧**一整天 0 条 ui-dialog 事件** —— jar 的二维码弹窗根本没送到宿主。
                    // 静默等于把故障藏起来：这里给一句人话，并落到 jar 自己的 Cookie 推送页
                    // （扫码走不通时这条是能用的登录路径）。
                    // 2026-10-03：文案去平台化。原句带「桥侧」（Android 上 jar 跑在进程内、
                    //   根本没有桥这个概念），按实际情形给一句人话。
                    await ShowToastAsync(
#if ANDROID
                        "扫码界面没能弹出——可改用「粘贴 Cookie」登录，或切换其它线路后再试。")
#else
                        "扫码界面没能弹出（jar 未上报界面事件）——可改用「粘贴 Cookie」登录。")
#endif
                        .ConfigureAwait(true);
                    // 同上：不再打开 do=config 网页（2026-10-03），改给一句人话。
                    await ShowToastAsync("该网盘的配置界面没能弹出（jar 未上报）。可改用「粘贴 Cookie」登录，或切换其它线路后再试。").ConfigureAwait(true);
                    return;
                }
            }

            // ② 解析链（playerContent + danmaku 钩子）完成后 jar 若弹窗，事件即时渲染；
            // 只留 800ms 跨进程到达缓冲——不再白等固定窗口
            var wait = WaitForDialogAsync(TimeSpan.FromMilliseconds(800));
            await provider.GetPlaySourcesAsync(site, item).ConfigureAwait(true);
            if (await wait.ConfigureAwait(true)) return;   // jar 已弹对话框/二维码（宿主已展示）

            // jar 没弹窗 → 给一句人话（2026-10-03 用户要求：删掉「打开 do=config 网页」这个兜底，
            // Windows 与 Android 都不再开 —— 那条 URL 指向宿主本地代理，Android 上 jar 跑在
            // 进程内，手机 WebView 打开只会看到「网页无法打开 net::ERR_HTTP_RESPONSE」）。
            await ShowToastAsync("该网盘的配置界面没能弹出（jar 未上报）。可改用「粘贴 Cookie」登录，或切换其它线路后再试。").ConfigureAwait(true);
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
