#if ANDROID
using AApp = Android.App.Application;
using AContext = Android.Content.Context;
using AWallpaper = Android.App.WallpaperManager;
#endif

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 订阅壁纸（对位 TVBox <c>ModelSettingFragment</c> 的「下载壁纸」+ <c>changeWallpaper</c>「还原」）。
/// <para>只有安卓有系统壁纸接口：Windows 侧 <c>SystemParametersInfo</c> 是 Win32，不在 MAUI/WinRT 的
/// 可达面里，所以这里<b>明确回「不支持」而不是静默成功</b> —— 静默成功会让用户以为设置过了。</para>
/// </summary>
public static class WallpaperService
{
    public static bool Supported
    {
        get
        {
#if ANDROID
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>下载并设为桌面壁纸；返回一句能直接显示给用户的结果。</summary>
    public static async Task<string> ApplyAsync(string url, HttpClient http)
    {
        if (!Supported) return "当前平台没有系统壁纸接口，无法设为桌面壁纸。";
        if (string.IsNullOrWhiteSpace(url)) return "订阅里没有 wallpaper 地址。";
        try
        {
            var bytes = await http.GetByteArrayAsync(url);
#if ANDROID
            if (bytes.Length == 0) return "壁纸内容为空。";
            var bmp = Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bmp is null) return "那串地址取回来的不是可解码的图片。";
            try
            {
                if (AApp.Context.GetSystemService(AContext.WallpaperService) is not AWallpaper wm)
                    return "系统壁纸服务不可用。";
                wm.SetBitmap(bmp);
                return "已设为桌面壁纸。";
            }
            finally
            {
                bmp.Recycle();
            }
#else
            return "";
#endif
        }
        catch (Exception ex)
        {
            return $"壁纸下载或设置失败：{ex.Message}";
        }
    }

    /// <summary>还原成系统默认壁纸。</summary>
    public static string Restore()
    {
        if (!Supported) return "当前平台没有系统壁纸接口。";
#if ANDROID
        try
        {
            if (AApp.Context.GetSystemService(AContext.WallpaperService) is not AWallpaper wm)
                return "系统壁纸服务不可用。";
            wm.Clear();
            return "已还原桌面壁纸。";
        }
        catch (Exception ex)
        {
            return $"还原失败：{ex.Message}";
        }
#else
        return "";
#endif
    }
}
