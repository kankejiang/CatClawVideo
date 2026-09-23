package com.github.tvbox.osc.util;

import android.app.Activity;

import java.util.Stack;

/**
 * TVBox 兼容层：宿主（猫爪影视）不使用 TVBox 的 App/Application 体系，
 * 但 Guard 系爬虫 jar（饭太硬 ftyshinidie 壳内 BaseSpiderGuard 家族）会反射
 * {@code AppManager.getInstance().currentActivity()} 取**当前前台 Activity**
 * 用于弹出网盘配置对话框（「已登录+启用中」列表、扫码登录浮层等）。
 *
 * <p>宿主在 MainActivity.OnResume 里通过 JNI 调 {@link #addActivity} 上报当前
 * Activity；jar 反射本类即可拿到可弹窗的 Activity。{@code currentActivity()}
 * 空栈返回 null（TVBox 原版会抛 EmptyStackException，这里容错）。</p>
 */
public class AppManager {
    private static final AppManager INSTANCE = new AppManager();
    private final Stack<Activity> activityStack = new Stack<>();

    private AppManager() {
    }

    public static AppManager getInstance() {
        return INSTANCE;
    }

    /** 前台 Activity 上报（宿主在 OnResume 调用；同实例重复上报只置顶不重复入栈）。 */
    public void addActivity(Activity activity) {
        if (activity == null) return;
        synchronized (activityStack) {
            activityStack.remove(activity);
            activityStack.push(activity);
        }
    }

    /** Activity 销毁时出栈（宿主 OnDestroy 调用）。 */
    public void finishActivity(Activity activity) {
        if (activity == null) return;
        synchronized (activityStack) {
            activityStack.remove(activity);
        }
    }

    /** 当前前台 Activity（栈顶）；无已上报 Activity 时返回 null。 */
    public Activity currentActivity() {
        synchronized (activityStack) {
            return activityStack.isEmpty() ? null : activityStack.lastElement();
        }
    }
}
