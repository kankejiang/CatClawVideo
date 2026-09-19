namespace CatClawVideo.Maui.Services;

/// <summary>
/// 统一的远程按键语义。Windows 的 <c>KeyDown</c> 与 Android 的 <c>KeyEvent</c>
/// 都先翻译成这里的枚举，再交给 <see cref="RemoteKeyRouter"/>，
/// 从而让一套焦点逻辑同时服务「键盘 / 鼠标」与「电视遥控器」。
/// </summary>
public enum RemoteKey
{
    Up,
    Down,
    Left,
    Right,
    /// <summary>确定 / 回车。</summary>
    Enter,
    /// <summary>返回 / Esc。</summary>
    Back,
}

/// <summary>
/// 可接收远程按键的区域（页面或页面内的一个分区）。
/// 采用「谁在最上层谁先处理」的栈式路由，二级页只需 Push 自己即可覆盖式接管按键。
///
/// <para>约定：无法处理时返回 <c>false</c>，交由外层（如主壳层顶栏）接手。</para>
/// </summary>
public interface IRemoteKeyHandler
{
    /// <summary>处理按键。<c>true</c> = 已消费。</summary>
    bool Handle(RemoteKey key);

    /// <summary>外层把焦点送进本区域（主壳层顶栏按下 ↓ / OK 时调用）。</summary>
    void FocusContent();

    /// <summary>
    /// 外层把焦点**收走**（主壳层顶栏按下 ↑ / ← / → 抢走焦点）时调用：
    /// 本区域必须清掉自己的高亮 —— 否则会出现「顶栏和内容区同时亮两个焦点」。
    /// </summary>
    void BlurContent();
}

/// <summary>
/// 远程按键路由：维护一个处理栈，栈顶优先。
///
/// <para>为什么自己做路由而不是依赖平台焦点系统：本应用的顶栏 tab 是
/// <c>Border + TapGestureRecognizer</c>（非原生可聚焦控件），平台的自动焦点查找覆盖不到它；
/// 而 Android 的 <c>Focusable</c> 与 Windows 的 Tab 焦点链语义又不一致。
/// 统一由应用自己管理「当前焦点项」，可以保证两端完全一致的行为与视觉。</para>
/// </summary>
public static class RemoteKeyRouter
{
    private static readonly List<IRemoteKeyHandler> _stack = new();

    /// <summary>当前生效的处理者（栈顶）。</summary>
    public static IRemoteKeyHandler? Current => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>入栈（页面出现时调用）。重复入栈会自动提到栈顶。</summary>
    public static void Push(IRemoteKeyHandler handler)
    {
        _stack.Remove(handler);
        _stack.Add(handler);
    }

    /// <summary>出栈（页面消失时调用）。</summary>
    public static void Pop(IRemoteKeyHandler handler) => _stack.Remove(handler);

    /// <summary>
    /// 从栈顶向下逐层询问，直到某层消费该按键。
    /// 「由内向外」的传递是必要的：设置页在最上层的导航栏按 ↑ 时返回 <c>false</c>，
    /// 于是按键落到主壳层顶栏，形成「内容 → 侧栏 → 顶部 tab」的自然走出路径。
    /// </summary>
    public static bool Handle(RemoteKey key)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_stack[i].Handle(key)) return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteKey] {_stack[i].GetType().Name}.{key} 异常：{ex.Message}");
            }
        }
        return false;
    }
}
