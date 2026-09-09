namespace CatClawVideo.Maui.Pages;

/// <summary>
/// spider 站点账号密码录入弹窗（alist 类源的登录凭据，保存至 spider-creds.json）。
/// 模态整页 + 居中卡片实现（跨平台，不依赖弹窗库）；
/// 保存后 JavaSpiderRuntime 加载站点时自动登录注入 token。
/// </summary>
public partial class CredentialsDialogPage : ContentPage
{
    private readonly string _serverHost;
    private readonly Entry _userEntry;
    private readonly Entry _passEntry;

    /// <summary>是否已保存凭据（true=保存成功关闭，false=取消关闭）</summary>
    public bool Saved { get; private set; }

    public CredentialsDialogPage(string siteName, string server)
    {
        _serverHost = new Uri(server).Authority;
        var existing = Core.Providers.SpiderCredentials.Get(server);

        BackgroundColor = Color.FromArgb("#B3000000");

        var res = Application.Current!.Resources;
        var card = new Border
        {
            WidthRequest = 380,
            BackgroundColor = (Color)res["CardBackgroundColor"],
            Stroke = (Color)res["DividerColor"],
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 24,
        };
        var stack = new VerticalStackLayout { Spacing = 12 };

        stack.Add(new Label
        {
            Text = "站点账号登录",
            FontSize = 18,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)res["TextPrimaryColor"],
        });
        stack.Add(new Label
        {
            Text = $"{siteName} · {_serverHost}\n该源需要账号密码，请输入后保存（保存到本地凭据文件）",
            FontSize = 12,
            TextColor = (Color)res["TextSecondaryColor"],
        });

        _userEntry = new Entry
        {
            Placeholder = "用户名",
            PlaceholderColor = (Color)res["TextHintColor"],
            TextColor = (Color)res["TextPrimaryColor"],
            Text = existing?.User ?? "",
        };
        _passEntry = new Entry
        {
            Placeholder = "密码",
            PlaceholderColor = (Color)res["TextHintColor"],
            TextColor = (Color)res["TextPrimaryColor"],
            IsPassword = true,
            Text = existing?.Pass ?? "",
        };
        stack.Add(_userEntry);
        stack.Add(_passEntry);

        var btnRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var saveBtn = new Button
        {
            Text = "保存",
            BackgroundColor = (Color)res["PrimaryButtonBackgroundColor"],
            TextColor = Colors.White,
            CornerRadius = 10,
            FontFamily = "OpenSansSemibold",
        };
        saveBtn.Clicked += async (_, _) =>
        {
            var user = _userEntry.Text?.Trim();
            var pass = _passEntry.Text ?? "";
            if (string.IsNullOrEmpty(user))
            {
                await DisplayAlertAsync("提示", "请输入用户名。", "确定");
                return;
            }
            try
            {
                Core.Providers.SpiderCredentials.Set(_serverHost, user, pass);
                Saved = true;
                await Navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("保存失败", ex.Message, "确定");
            }
        };
        var cancelBtn = new Button
        {
            Text = "取消",
            BackgroundColor = Colors.Transparent,
            TextColor = (Color)res["TextHintColor"],
            CornerRadius = 10,
        };
        cancelBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        btnRow.Add(saveBtn, 0);
        btnRow.Add(cancelBtn, 1);
        stack.Add(btnRow);

        card.Content = stack;
        Content = new Grid
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { card },
        };
    }
}
