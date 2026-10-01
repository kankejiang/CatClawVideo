namespace StreamProbe;

/// <summary>
/// Android KeyEvent 键码的最小子集 + 名称。
///
/// <para><b>键码语义是协议的一个留白</b>（README 只规定 u32 keycode）：本工具统一用
/// <b>Android keycodes</b>（<c>android.view.KeyEvent</c> 的常量值），理由是 T3 注入端在
/// Android 侧可以直接 <c>input keyevent &lt;code&gt;</c>，零映射。已在本工具 README 的
/// 「协议建议」里提出，待三线联调时把语义补进 docs/tasks/README.md（由维护者定稿）。</para>
/// </summary>
public static class AndroidKeys
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
    public const uint KeycodeTab = 61;
    public const uint KeycodeEnter = 66;
    public const uint KeycodeEsc = 111;
    public const uint KeycodeSpace = 62;
    public const uint KeycodeDel = 67;      // Backspace
    // 字母/数字与 ASCII 对齐：KEYCODE_A=29 … Z=54，KEYCODE_0=7 … 9=16
    public const uint KeycodeA = 29;
    public const uint Keycode0 = 7;

    /// <summary>键码 → 名称（打印/日志友好；查不到返回 0xNN 形式）。</summary>
    public static string Name(uint code) => code switch
    {
        KeycodeHome => "HOME", KeycodeBack => "BACK",
        KeycodeDpadUp => "DPAD_UP", KeycodeDpadDown => "DPAD_DOWN",
        KeycodeDpadLeft => "DPAD_LEFT", KeycodeDpadRight => "DPAD_RIGHT",
        KeycodeDpadCenter => "DPAD_CENTER",
        KeycodeVolumeUp => "VOL_UP", KeycodeVolumeDown => "VOL_DOWN",
        KeycodePower => "POWER", KeycodeTab => "TAB",
        KeycodeEnter => "ENTER", KeycodeEsc => "ESC",
        KeycodeSpace => "SPACE", KeycodeDel => "DEL",
        >= KeycodeA and <= KeycodeA + 25 => ((char)('A' + code - KeycodeA)).ToString(),
        >= Keycode0 and <= Keycode0 + 9 => ((char)('0' + code - Keycode0)).ToString(),
        _ => $"0x{code:X}",
    };

    /// <summary>Windows Forms Keys → Android keycode（窄映射：方向键/回车/ESC/字母数字等）。</summary>
    public static uint FromWinForms(Keys k) => k switch
    {
        Keys.Up => KeycodeDpadUp, Keys.Down => KeycodeDpadDown,
        Keys.Left => KeycodeDpadLeft, Keys.Right => KeycodeDpadRight,
        Keys.Enter => KeycodeEnter, Keys.Escape => KeycodeEsc,
        Keys.Space => KeycodeSpace, Keys.Back => KeycodeDel,
        Keys.Tab => KeycodeTab,
        >= Keys.A and <= Keys.Z => KeycodeA + (uint)(k - Keys.A),
        >= Keys.D0 and <= Keys.D9 => Keycode0 + (uint)(k - Keys.D0),
        _ => Unknown,
    };
}
