package com.github.catvod.crawler;

import android.content.Context;

import org.json.JSONObject;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import okhttp3.Dns;
import okhttp3.OkHttpClient;

/**
 * TVBox {@code Spider} base class stub (host-provided; spider jars never bundle it).
 *
 * <p>Kept in lock-step with the reference implementation
 * ({@code app/src/main/java/com/github/catvod/crawler/Spider.java}). Members that pull in
 * TVBox-internal packages are re-implemented locally instead of dropped, because jars compiled
 * against the reference build link against them by name and fail at resolution time if absent:</p>
 * <ul>
 *   <li>{@code initApi(SpiderApi)} - {@code XBPQ} (the 饭太硬/海阔 family) overrides it and calls
 *       {@code super.initApi(api)}; without the method on this class the override fails to link.</li>
 *   <li>{@code client()} / {@code safeDns()} - backed by the okhttp3 jar the host already ships,
 *       instead of TVBox's {@code com.github.catvod.net.OkHttp}.</li>
 * </ul>
 */
public class Spider {

    public static JSONObject empty = new JSONObject();

    public String siteKey;

    protected static Context mContext;

    private static volatile OkHttpClient client;

    public void init(Context context) {
        mContext = context;
    }

    public void init(Context context, String extend) {
        init(context);
    }

    /**
     * Injects the host-side helper object. Called by the host right after instantiation and
     * before {@code init()}; spiders that need {@code SpiderApi} override this and must call
     * {@code super}.
     */
    public void initApi(SpiderApi api) {
    }

    public String homeContent(boolean filter) {
        return "";
    }

    public String homeVideoContent() {
        return "";
    }

    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        return "";
    }

    public String detailContent(List<String> ids) {
        return "";
    }

    public String searchContent(String key, boolean quick) {
        return "";
    }

    public String searchContent(String key, boolean quick, String pg) {
        return searchContent(key, quick);
    }

    public String playerContent(String flag, String id, List<String> vipFlags) {
        return "";
    }

    public boolean isVideoFormat(String url) {
        return false;
    }

    public boolean manualVideoCheck() {
        return false;
    }

    public String liveContent(String url) {
        return "";
    }

    public static Dns safeDns() {
        return Dns.SYSTEM;
    }

    public static OkHttpClient client() {
        if (client == null) {
            synchronized (Spider.class) {
                if (client == null) {
                    client = new OkHttpClient.Builder()
                            .connectTimeout(15, TimeUnit.SECONDS)
                            .readTimeout(20, TimeUnit.SECONDS)
                            .writeTimeout(20, TimeUnit.SECONDS)
                            .build();
                }
            }
        }
        return client;
    }

    public void cancelByTag() {
    }

    public void destroy() {
    }

    public Object[] proxyLocal(Map<String, String> params) {
        return null;
    }

    public Object[] proxy(Map<String, String> params) {
        return proxyLocal(params);
    }

    public String action(String action) {
        return null;
    }
}
