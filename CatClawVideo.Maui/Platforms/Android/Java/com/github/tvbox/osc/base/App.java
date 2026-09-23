package com.github.tvbox.osc.base;

import android.app.Activity;
import android.app.Application;
import android.content.Context;

import com.github.tvbox.osc.util.AppManager;

/**
 * TVBox 兼容层：Guard 系爬虫 jar（饭太硬 ftyshinidie 壳内 BaseSpiderGuard 家族）
 * 反射 {@code com.github.tvbox.osc.base.App.getInstance()} 取宿主 Application 单例，
 * 再调 {@link #getCurrentActivity()} 取当前前台 Activity 用于弹网盘配置对话框。
 *
 * <p>宿主（猫爪影视）的 Application 是 MAUI 的 C# 实现，无法让本类替代之，
 * 因此本类是**独立壳单例**：{@link #getInstance()} 懒创建自身；
 * {@link #attachHost(Application)} 由宿主启动时注入真实 Application，
 * 本类把 Context 语义（getResources/getSystemService/getPackageName 等）代理过去。</p>
 */
public class App extends Application {
    private static App instance;
    private static Application host;

    public static App getInstance() {
        if (instance == null) {
            synchronized (App.class) {
                if (instance == null) {
                    instance = new App();
                    Application hostApp = host;
                    if (hostApp != null) {
                        try {
                            instance.attachBaseContext(hostApp.getBaseContext());
                        } catch (Throwable ignored) {
                        }
                    }
                }
            }
        }
        return instance;
    }

    /** 宿主启动时注入真实 Application（MainApplication），供 Context 语义代理。 */
    public static void attachHost(Application application) {
        host = application;
        App self = instance;
        if (self != null && application != null) {
            try {
                self.attachBaseContext(application.getBaseContext());
            } catch (Throwable ignored) {
            }
        }
    }

    /** 当前前台 Activity（TVBox 同名语义，委托 AppManager 栈顶）。 */
    public Activity getCurrentActivity() {
        return AppManager.getInstance().currentActivity();
    }

    // ── Context 语义代理到宿主 Application（未注入时退化为 super 的行为） ──

    @Override
    public Context getApplicationContext() {
        return host != null ? host.getApplicationContext() : super.getApplicationContext();
    }

    @Override
    public android.content.res.Resources getResources() {
        return host != null ? host.getResources() : super.getResources();
    }

    @Override
    public Object getSystemService(String name) {
        return host != null ? host.getSystemService(name) : super.getSystemService(name);
    }

    @Override
    public String getPackageName() {
        return host != null ? host.getPackageName() : super.getPackageName();
    }
}
