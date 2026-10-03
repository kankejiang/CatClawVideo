namespace CatClawVideo.Streaming;

/// <summary>
/// Android <c>KeyEvent</c> 键码（协议里 keycode 的语义）+ 从 <b>Win32 虚拟键码</b>的映射。
///
/// <para>键码语义沿用 T2 的决定（<c>tools/stream-probe/AndroidKeys.cs</c> 的注释）：统一用
/// <c>android.view.KeyEvent</c> 的常量值 ⇒ 注入端可以 <c>input keyevent &lt;code&gt;</c> 零映射。</para>
///
/// <para>这里刻意<b>不</b>依赖 <c>System.Windows.Forms.Keys</c> 或 <c>Windows.System.VirtualKey</c>
/// 类型（本库要能被 android 头一起编译）：映射的入参用 <see cref="int"/>，
/// 值就是 Win32 VK 码 —— WinUI 的 <c>VirtualKey</c> 枚举数值与 Win32 VK 一致，
/// 所以平台侧直接 <c>(int)e.Key</c> 传进来即可。</para>
/// </summary>
public static class AndroidKeycodes
{
    public const uint Unknown = 0;

    public const uint KeycodeHome = 3;
    public const uint KeycodeBack = 4;
    public const uint KeycodeDpadUp = 19;
    public const uint KeycodeDpadDown = 20;
    public const uint KeycodeDpadLeft = 21;
    public const uint KeycodeDpadRight = 22;
    public const uint KeycodeDpadCenter = 23;
    public const uint KeycodeVolumeUp = 24;
    public const uint KeycodeVolumeDown = 25;
    public const uint KeycodePower = 26;
    public const uint KeycodeA = 29;
    public const uint Keycode0 = 7;
    public const uint KeycodeTab = 61;
    public const uint KeycodeSpace = 62;
    public const uint KeycodeEnter = 66;
    public const uint KeycodeDel = 67;          // Backspace
    public const uint KeycodeEsc = 111;
    public const uint KeycodePageUp = 92;
    public const uint KeycodePageDown = 93;
    public const uint KeycodeMoveHome = 122;    // 文本语义的 Home
    public const uint KeycodeMoveEnd = 123;
    public const uint KeycodeForwardDel = 112;  // Delete

    /// <summary>Win32 VK 码 → Android keycode；映射不到返回 <see cref="Unknown"/>。</summary>
    public static uint FromVirtualKey(int vk) => vk switch
    {
        0x26 => KeycodeDpadUp,                  // VK_UP
        0x28 => KeycodeDpadDown,                // VK_DOWN
        0x25 => KeycodeDpadLeft,                // VK_LEFT
        0x27 => KeycodeDpadRight,               // VK_RIGHT
        0x0D => KeycodeEnter,                    // VK_RETURN
        0x1B => KeycodeEsc,                      // VK_ESCAPE
        0x20 => KeycodeSpace,                    // VK_SPACE
        0x08 => KeycodeDel,                      // VK_BACK
        0x09 => KeycodeTab,                      // VK_TAB
        0x2E => KeycodeForwardDel,               // VK_DELETE
        0x21 => KeycodePageUp,                   // VK_PRIOR
        0x22 => KeycodePageDown,                 // VK_NEXT
        0x24 => KeycodeMoveHome,                 // VK_HOME
        0x23 => KeycodeMoveEnd,                  // VK_END
        >= 0x41 and <= 0x5A => KeycodeA + (uint)(vk - 0x41),        // A..Z
        >= 0x30 and <= 0x39 => Keycode0 + (uint)(vk - 0x30),        // 0..9
        _ => Unknown,
    };

    /// <summary>
    /// ESC 的取舍（写死在这里，免得两条线各做一遍）：远程画面页上 <b>Esc = 退出页面</b>（N4-2 要求），
    /// 所以不往 Android 发 0x1B；需要"安卓返回"用语义等价键 <see cref="KeycodeBack"/>
    /// （页面右键、以及界面上的「返回」按钮都发它）。
    /// </summary>
    public const uint ExitReservedKey = KeycodeEsc;

    /// <summary>键码 → 名称（状态栏/日志用；查不到返回 0xNN）。</summary>
    public static string Name(uint code) => code switch
    {
        KeycodeHome => "HOME", KeycodeBack => "BACK",
        KeycodeDpadUp => "UP", KeycodeDpadDown => "DOWN",
        KeycodeDpadLeft => "LEFT", KeycodeDpadRight => "RIGHT",
        KeycodeDpadCenter => "CENTER",
        KeycodeVolumeUp => "VOL+", KeycodeVolumeDown => "VOL-",
        KeycodePower => "POWER", KeycodeTab => "TAB",
        KeycodeEnter => "ENTER", KeycodeEsc => "ESC",
        KeycodeSpace => "SPACE", KeycodeDel => "DEL",
        KeycodeForwardDel => "DELETE", KeycodePageUp => "PGUP", KeycodePageDown => "PGDN",
        KeycodeMoveHome => "HOME(文本)", KeycodeMoveEnd => "END",
        >= KeycodeA and <= KeycodeA + 25 => ((char)('A' + code - KeycodeA)).ToString(),
        >= Keycode0 and <= Keycode0 + 9 => ((char)('0' + code - Keycode0)).ToString(),
        _ => $"0x{code:X}",
    };
}
