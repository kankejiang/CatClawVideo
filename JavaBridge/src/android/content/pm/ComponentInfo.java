package android.content.pm;

/**
 * <code>android.content.pm.ComponentInfo</code> 桩 —— 真机 ProviderInfo/ActivityInfo/ServiceInfo
 * 的直接父类（标记父类，{@code ComponentInfo → PackageItemInfo}）。
 *
 * <p>桩以前让 ProviderInfo 直接挂 PackageItemInfo（少一层），壳里按 ComponentInfo 处理
 * 组件信息（enabled 状态、packageName 归属）的代码需要这一层才过 ART 校验。</p>
 */
public class ComponentInfo extends PackageItemInfo {
}
