using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// A 路线渲染页（2026-10-02）：把桥上行「整棵 View 树」（ui-dialog 的 <c>tree</c> 字段，
/// 带 jar 设置的文本/字号/颜色/padding/背景圆角/方向/位图）在 WebView 里 1:1 还原成
/// jar 作者设计的界面（对齐真机 TVBox 观感），不再按宿主主题画胶囊按钮。
///
/// <para><b>交互</b>：树里带 <c>i</c> 的节点 = 可点击（与旧 rows 的节点下标一致），点击经
/// WebMessage → <c>ui-result</c> 回传，点完不关框（jar 的自定义视图框语义）；
/// jar 就地 setText 后桥上行 <c>ui-rows</c>（带新 tree）→ <see cref="UpdateTree"/> 整树换新。</para>
///
/// <para><b>关闭</b>：右上角 ✕、点遮罩、Back/Esc → <c>ui-result which=-2</c>（onCancel 语义）；
/// jar 主动 dismiss 走 <c>ui-dismiss</c> 由 <see cref="SpiderUiHost"/> 出栈。</para>
/// </summary>
public partial class SpiderTreeDialogPage : ContentPage, IRemoteKeyHandler
{
    private readonly Action<int> _onPick;
    private readonly Action? _onCancel;
    private WebView _web = null!;
    private bool _loaded;

    public SpiderTreeDialogPage(int seq, string treeJson, Action<int> onPick, Action? onCancel = null)
    {
        _onPick = onPick;
        _onCancel = onCancel;
        BackgroundColor = Color.FromArgb("#B3000000");

        _web = new WebView
        {
            Source = new HtmlWebViewSource { Html = BuildHtml(treeJson) },
        };
        // MAUI WebView 没有跨平台 WebMessageReceived：JS 用自定义 scheme 导航 + Navigating 拦截
        //（clsk:clk:<节点下标> / clsk:cancel）
        _web.Navigating += OnNavigating;

        // 右上角 ✕：卡片由 HTML 居中渲染，按钮浮在页面角上保证始终可见
        var closeBtn = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 14, 14, 0),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#55000000"),
            Content = new Label
            {
                Text = "✕",
                FontSize = 16,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };
        var closeTap = new TapGestureRecognizer();
        closeTap.Tapped += async (_, _) => await CloseAsync();
        closeBtn.GestureRecognizers.Add(closeTap);

        Content = new Grid { Children = { _web, closeBtn } };
    }

    /// <summary>jar 就地改了文本（ui-rows 带新 tree）→ 整树换新。</summary>
    public void UpdateTree(string treeJson)
    {
        if (!_loaded) return;
        _ = _web.EvaluateJavaScriptAsync($"window.__setTree({treeJson}); void 0;");
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!e.Url.StartsWith("clsk:", StringComparison.Ordinal)) return;
        e.Cancel = true;
        var msg = e.Url["clsk:".Length..];
        if (msg == "cancel") { _ = CloseAsync(); return; }
        if (msg.StartsWith("clk:", StringComparison.Ordinal) &&
            int.TryParse(msg.AsSpan(4), out var idx))
        {
            _onPick(idx);   // 点可点节点不关框（jar 的自定义视图框语义），由桥 dismiss 决定关
        }
    }

    private async Task CloseAsync()
    {
        RemoteKeyRouter.Pop(this);
        _onCancel?.Invoke();
        try { if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync(); } catch { }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RemoteKeyRouter.Push(this);
    }

    protected override void OnDisappearing()
    {
        RemoteKeyRouter.Pop(this);
        base.OnDisappearing();
    }

    /// <summary>吃掉全部按键（模态）；Back/Esc 关框并回传取消。</summary>
    public bool Handle(RemoteKey key)
    {
        if (key == RemoteKey.Back) { _ = CloseAsync(); return true; }
        return true;
    }

    public void FocusContent() { }

    public void BlurContent() { }

    // ═══════════ HTML 渲染器 ═══════════

    private static string BuildHtml(string treeJson)
    {
        // "</script>" 防提前闭合（树里的文本来自 jar，理论可能出现）
        treeJson = treeJson.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
html,body{margin:0;padding:0;width:100%;height:100%;background:rgba(0,0,0,.55);
  font-family:'Segoe UI',Roboto,'Microsoft YaHei',sans-serif;
  display:flex;align-items:center;justify-content:center;overflow:hidden}
#wrap{max-width:92vw;max-height:86vh;display:flex;flex-direction:column;border-radius:14px;
  overflow:hidden;box-shadow:0 12px 48px rgba(0,0,0,.45)}
#scroll{overflow-y:auto}
.vg{display:flex;flex-direction:column}
.vg.row{flex-direction:row}
.tv{white-space:pre-wrap;word-break:break-word}
.clk{cursor:pointer}
.clk:hover{filter:brightness(.96)}
.dis{opacity:.45;pointer-events:none}
img{max-width:100%;display:block}
</style></head>
<body><div id="wrap"><div id="scroll"><div id="root"></div></div></div>
<script>
const TREE = {{treeJson}};
function hex(c){
  if(c==null) return '';
  c = c>>>0;
  const a=((c>>>24)&255)/255, r=(c>>>16)&255, g=(c>>>8)&255, b=c&255;
  if(a>=1) return 'rgb('+r+','+g+','+b+')';
  return 'rgba('+r+','+g+','+b+','+a.toFixed(3)+')';
}
function gravityStyle(el,g,isText){
  if(g==null) return;
  if(g&0x01){ if(isText) el.style.textAlign='center'; else el.style.justifyContent='center'; }
  else if(g&0x05){ if(isText) el.style.textAlign='right'; else el.style.justifyContent='flex-end'; }
  else if(g&0x07){ if(isText) el.style.textAlign='left'; else el.style.justifyContent='flex-start'; }
  if(g&0x10){ el.style.alignItems='center'; }
  else if(g&0x50){ el.style.alignItems='flex-end'; }
  else if(g&0x30){ el.style.alignItems='flex-start'; }
}
function applyCommon(el,n){
  if(n.p&&n.p.length===4) el.style.padding=n.p[0]+'px '+n.p[3]+'px '+n.p[2]+'px '+n.p[1]+'px';
  if(n.bg&&n.bg.c!=null){
    el.style.background=hex(n.bg.c);
    if(n.bg.r) el.style.borderRadius=n.bg.r+'px';
  }
  if(n.w) el.style.width=n.w+'px';
  if(n.h) el.style.height=n.h+'px';
  if(n.wm) el.style.alignSelf='stretch';
  if(n.gone) el.style.display='none';
  if(n.dis) el.classList.add('dis');
}
function build(n){
  const el=document.createElement('div');
  const k=(n.k||'');
  const isText = (k==='TextView'||k==='Button'||k==='EditText');
  if(k==='ImageView'){
    el.classList.add('vg');
    if(n.img&&n.img.png){
      const im=document.createElement('img');
      im.src='data:image/png;base64,'+n.img.png;
      im.width=n.img.w||n.w||240; im.height=n.img.h||n.h||240;
      el.appendChild(im);
    }
  } else if(isText){
    el.classList.add('tv');
    el.textContent=n.t||'';
    if(n.ts) el.style.fontSize=n.ts+'px';
    if(n.tc!=null) el.style.color=hex(n.tc);
    gravityStyle(el,n.g,true);
  } else {
    el.classList.add('vg');
    if(n.o===0) el.classList.add('row');
    gravityStyle(el,n.g,false);
  }
  if(n.wm){ el.style.width='100%'; }
  applyCommon(el,n);
  if(n.i!=null){
    el.classList.add('clk');
    el.dataset.i=n.i;
    el.addEventListener('click',function(ev){ ev.stopPropagation(); clk(n.i); });
  }
  (n.c||[]).forEach(function(ch){ el.appendChild(build(ch)); });
  return el;
}
function clk(i){
      window.location='clsk:clk:'+i;
    }
    function setTree(tree){
      const root=document.getElementById('root');
      root.innerHTML='';
      const el=build(tree);
      if(tree.bg&&tree.bg.c!=null){ document.getElementById('wrap').style.background=hex(tree.bg.c); }
      root.appendChild(el);
    }
    window.__setTree=function(tree){ setTree(tree); };
    setTree(TREE);
    window.location='clsk:ready';
</script></body></html>
""";
    }
}
