package com.github.catvod.crawler;

import android.content.Context;
import java.util.HashMap;
import java.util.List;

/** TVBox Spider 基类 stub（宿主提供；spider jar 不打包它）。 */
public abstract class Spider {
    public void init(Context context, String extend) { }
    public String init(String extend) { return ""; }
    public String homeContent(boolean filter) { return ""; }
    public String homeVideoContent() { return ""; }
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) { return ""; }
    public String detailContent(List<String> ids) { return ""; }
    public String searchContent(String key, boolean quick) { return ""; }
    public String searchContent(String key, boolean quick, String pg) { return ""; }
    public String playerContent(String flag, String id, List<String> vipFlags) { return ""; }
    public String action(String action) { return ""; }
    public boolean isVideoFormat(String url) { return false; }
    public boolean manualVideoCheck() { return false; }
    public void destroy() { }
}
