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
 * Host-provided base class for TVBox-compatible spider jars.
 *
 * <p>Third-party spider jars DO NOT bundle this class. Every spider class inside such a
 * jar extends it, so the host application must publish an identical class on its own
 * class loader. Guard-hardened packages (e.g. the ftyshinidie package, whose classes are
 * all named {@code *Guard} and delegate to a native-decrypted implementation) inherit it
 * too - {@code BaseSpiderGuard} extends this class directly.</p>
 *
 * <p>If this class is missing, {@code DexClassLoader.loadClass()} cannot resolve the
 * superclass of any spider class and throws {@code NoClassDefFoundError} instead. The
 * caller sees a bare "class not found" and the whole jar looks unusable.</p>
 *
 * <p>Member parity with the reference implementation
 * ({@code app/src/main/java/com/github/catvod/crawler/Spider.java}) is required, not
 * cosmetic: jars link against these members by name, and verification of the whole spider
 * class fails when one is absent. Two groups are easy to get wrong:</p>
 * <ul>
 *   <li>{@code initApi(SpiderApi)} - overridden by {@code XBPQ} (the 饭太硬/海阔 family),
 *       which calls {@code super.initApi(api)}. Dropping it here breaks every {@code csp_XBPQ}
 *       site even though the site itself is otherwise fine.</li>
 *   <li>{@code client()} / {@code safeDns()} - re-implemented on top of the okhttp3 jar the
 *       host already ships, instead of TVBox's {@code com.github.catvod.net.OkHttp}.</li>
 * </ul>
 *
 * <p>The host must also <em>call</em> these in the reference order
 * ({@code siteKey} -> {@code initApi} -> {@code init}), exactly as TVBox's
 * {@code JarLoader.getSpider()} does; see {@code DexSpiderRuntime}.</p>
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

    /**
     * Home page content.
     *
     * @param filter whether filtering is enabled
     */
    public String homeContent(boolean filter) {
        return "";
    }

    /**
     * Home page "recently updated" content, for spiders that report it separately.
     */
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

    /**
     * Used while parsing in a WebView; spiders may override to judge whether a loaded
     * url is a video url.
     */
    public boolean isVideoFormat(String url) {
        return false;
    }

    /**
     * Whether the host should let the spider manually inspect urls loaded in a WebView.
     */
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

    /**
     * Cancel in-flight requests by tag.
     */
    public void cancelByTag() {
    }

    public void destroy() {
    }

    /**
     * Spider-side proxy entry, served through the host's local proxy server.
     */
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
