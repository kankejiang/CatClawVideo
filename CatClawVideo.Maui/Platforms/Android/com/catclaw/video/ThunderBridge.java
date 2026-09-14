package com.catclaw.video;

import android.content.Context;
import android.content.SharedPreferences;
import android.text.TextUtils;
import android.util.Log;

import com.xunlei.downloadlib.XLTaskHelper;
import com.xunlei.downloadlib.android.XLUtil;
import com.xunlei.downloadlib.parameter.BtSubTaskDetail;
import com.xunlei.downloadlib.parameter.TorrentFileInfo;
import com.xunlei.downloadlib.parameter.TorrentInfo;
import com.xunlei.downloadlib.parameter.XLTaskInfo;

import java.io.File;
import java.util.ArrayList;
import java.util.List;
import java.util.Random;

/**
 * 迅雷下载引擎（磁力）宿主桥。
 *
 * <p>流程对齐 TVBox {@code util/thunder/Thunder.java}：
 * <pre>
 *   init()                                   // 伪造 IMEI/MAC + XLTaskHelper.init
 *   listFiles(magnet)  → addMagentTask + getTorrentInfo().mSubFileInfo   // 文件列表（= TVBox 的"11 集"）
 *   playFile(index)    → addTorrentTask + 轮询状态为可播 + getLoclUrl()   // → http://127.0.0.1:&lt;port&gt;/本地路径
 * </pre>
 *
 * <p><b>为什么必须在 Java 侧写</b>（与荐片 P2PClass 同理）：{@code XLTaskHelper} 的静态块经
 * {@code XLLoader} 调 {@code System.loadLibrary("xl_thunder_sdk")}，而 {@code System.loadLibrary}
 * 用 {@code Reflection.getCallerClass()} 定位 nativeLibraryDir —— 从 C# 经 JNI 调用没有 Java 帧，
 * caller 会判成 {@code java.lang.System}(boot) 只搜 /system/lib64，必然 UnsatisfiedLinkError。
 * 这里由 Java 帧发起，classloader 就是 App 的，能正确命中 lib/arm64/。</p>
 *
 * <p><b>凭据与隐私</b>：appKey 是硬编码字符串（TVBox 同款，非本地生成）；
 * IMEI/MAC 用随机值伪造成并持久化，<b>不读取真实设备标识</b>。</p>
 *
 * <p>⚠️ 许可：{@code libxl_thunder_sdk.so} / {@code libxl_stat.so} / {@code thunder.jar} 取自
 * TVBox（AGPL-3.0）仓库，本体为迅雷商业闭源 SDK；其 appKey 属未授权凭据。
 * 用户已知情并选择打包，见项目根 THIRD-PARTY-NOTICES.md。</p>
 */
public final class ThunderBridge {

    private static final String TAG = "ThunderBridge";
    /** 新版=新版；对齐 TVBox 传入的 SDK 版本串 */
    private static final String SDK_VERSION = "21.01.07.800002";
    private static final String PREF_FAKE_IDS = "rand_thunder_id";

    private static volatile boolean sInited;
    private static long sTaskId;
    private static String sCacheRoot;
    private static TorrentFileInfo[] sFiles;
    /** getTorrentInfo() 的入参：addMagentTask 落盘后的种子文件绝对路径 */
    private static String sTorrentFilePath;

    // ⚠️ 去重缓存：listFiles/playFile 开头都会 stopCurrent()，而同一内容常被解析两次
    // （UI 重入 / 播发失败重试）—— 第二次会把**正在播放的任务**停掉，
    // 播放中的 mkv 流被截断，报 IllegalStateException: No valid varint length mask found。
    // 因此同一磁力、同一文件必须直接复用，不做任何 stop/add。
    private static String sLastMagnet;
    private static String sLastListResult;
    private static String sLastPlayKey;
    private static String sLastPlayUrl;

    private ThunderBridge() {
    }

    // ─────────────────────── ① 初始化 ───────────────────────

    /**
     * 初始化一个下载引擎实例。幂等。
     *
     * @param ctx       宿主 Context
     * @param cacheRoot 下载根目录（会在其下按种子名建子目录）
     * @return {@code "OK:<sdk版本>"} 或 {@code "ERR:<原因>"}
     */
    public static synchronized String init(Context ctx, String cacheRoot) {
        if (sInited) {
            return "OK:already";
        }
        try {
            sCacheRoot = cacheRoot;
            File root = new File(cacheRoot);
            if (!root.exists() && !root.mkdirs()) {
                return "ERR:无法创建缓存目录 " + cacheRoot;
            }

            // 伪造 IMEI/MAC（对齐 TVBox：不读真实设备标识），并持久化保持同机稳定
            SharedPreferences sp = ctx.getSharedPreferences(PREF_FAKE_IDS, Context.MODE_PRIVATE);
            String imei = sp.getString("imei", null);
            String mac = sp.getString("mac", null);
            if (imei == null) {
                imei = randomString("0123456", 15);
                sp.edit().putString("imei", imei).apply();
            }
            if (mac == null) {
                mac = randomString("ABCDEF0123456", 12).toUpperCase();
                sp.edit().putString("mac", mac).apply();
            }
            XLUtil.mIMEI = imei;
            XLUtil.isGetIMEI = true;
            XLUtil.mMAC = mac;
            XLUtil.isGetMAC = true;

            // appKey：TVBox 同款拼法（两段硬编码常量拼接）
            String cd3 = "cee25055f125a2fde0";
            String base64Decode = "axzNjAwMQ^^yb==0^852^083dbcff^";
            String cd = base64Decode.substring(1) + cd3.substring(0, cd3.length() - 1);

            XLTaskHelper.init(ctx, cd, SDK_VERSION);
            // XLTaskHelper 上没有 getDownloadLibVersion（那是 XLDownloadManager 的），
            // 用 instance() 能否取到单例作为就绪判据。
            if (XLTaskHelper.instance() == null) {
                return "ERR:XLTaskHelper.instance() 为空";
            }
            sInited = true;
            Log.i(TAG, "迅雷引擎就绪（SDK " + SDK_VERSION + "）");
            return "OK:" + SDK_VERSION;
        } catch (Throwable t) {
            return "ERR:" + rootCause(t);
        }
    }

    public static synchronized boolean isReady() {
        return sInited;
    }

    public static synchronized String version() {
        return sInited ? SDK_VERSION : null;
    }

    // ─────────────────────── ② 文件列表 ───────────────────────

    /**
     * 解析磁力并返回种子内的文件列表（这是 TVBox「一个磁力展开成多集」的来源）。
     *
     * <p>返回格式（每行一个文件，制表符分隔）：{@code "<index>\t<size>\t<相对路径>"}；
     * 失败返回 {@code "ERR:<原因>"}。调用方自行按扩展名筛选视频。</p>
     *
     * @param magnet     磁力链接
     * @param preferName 期望的种子文件名（可为 null，内部按磁力推导）
     */
    public static synchronized String listFiles(String magnet, String preferName) {
        if (!sInited) return "ERR:引擎未初始化";
        if (TextUtils.isEmpty(magnet)) return "ERR:磁力为空";
        // 同一磁力已解析过 → 直接复用，不要碰正在运行的任务
        if (magnet.equals(sLastMagnet) && sLastListResult != null) {
            return sLastListResult;
        }
        try {
            XLTaskHelper helper = XLTaskHelper.instance();

            String fileName = TextUtils.isEmpty(preferName) ? helper.getFileName(magnet) : preferName;
            if (TextUtils.isEmpty(fileName)) return "ERR:无法推导种子文件名";

            File torrentFile = new File(sCacheRoot, fileName);
            stopCurrent();

            long t0 = System.currentTimeMillis();
            sTaskId = helper.addMagentTask(magnet, sCacheRoot, fileName);

            // 等迅雷把种子元数据落盘（TVBox 是轮询 mTaskStatus==2）
            TorrentInfo ti = null;
            for (int i = 0; i < 100; i++) {
                XLTaskInfo xi = helper.getTaskInfo(sTaskId);
                if (xi != null && xi.mTaskStatus == 3) {
                    return "ERR:任务失败 mErrorCode=" + xi.mErrorCode;
                }
                ti = helper.getTorrentInfo(torrentFile.getAbsolutePath());
                if (ti != null && ti.mSubFileInfo != null && ti.mSubFileInfo.length > 0) {
                    break;
                }
                Thread.sleep(100);
            }
            if (ti == null || ti.mSubFileInfo == null || ti.mSubFileInfo.length == 0) {
                return "ERR:解析种子超时（" + (System.currentTimeMillis() - t0) + "ms）";
            }

            sTorrentFilePath = torrentFile.getAbsolutePath();
            sFiles = ti.mSubFileInfo;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sFiles.length; i++) {
                TorrentFileInfo f = sFiles[i];
                if (f == null) continue;
                // torrentPath 交给 playFile 用；这里只回传清单
                f.torrentPath = sTorrentFilePath;
                sb.append(f.mFileIndex).append('\t')
                  .append(f.mFileSize).append('\t')
                  .append(f.mFileName == null ? "" : f.mFileName).append('\n');
            }
            Log.i(TAG, "磁力解析成功：" + sFiles.length + " 个文件，用时 "
                    + (System.currentTimeMillis() - t0) + "ms");
            sLastMagnet = magnet;
            sLastListResult = sb.toString();
            return sLastListResult;
        } catch (Throwable t) {
            return "ERR:" + rootCause(t);
        }
    }

    // ─────────────────────── ③ 播放某个文件 ───────────────────────

    /**
     * 让迅雷开始下载第 {@code index} 个文件（= mFileIndex），并返回可播的本地 http 地址。
     * 状态 1/2/4 都算可播（边下边播），与 TVBox 一致。
     *
     * @return {@code "OK:<url>"} 或 {@code "ERR:<原因>"}
     */
    public static synchronized String playFile(int index) {
        if (!sInited) return "ERR:引擎未初始化";
        if (sFiles == null || sTorrentFilePath == null) return "ERR:尚未解析文件列表";

        TorrentFileInfo target = null;
        for (TorrentFileInfo f : sFiles) {
            if (f != null && f.mFileIndex == index) {
                target = f;
                break;
            }
        }
        if (target == null) return "ERR:文件索引 " + index + " 不存在";

        // 同一文件且任务仍在跑 → 直接复用地址，绝不 stop（stop 会截断正在播放的流）
        String playKey = sTorrentFilePath + "#" + index;
        if (sTaskId > 0 && playKey.equals(sLastPlayKey) && !TextUtils.isEmpty(sLastPlayUrl)) {
            return "OK:" + sLastPlayUrl;
        }

        try {
            XLTaskHelper helper = XLTaskHelper.instance();
            stopCurrent();

            // 子目录名 = 种子文件名去扩展名（TVBox 同款）
            String torrentName = new File(sTorrentFilePath).getName();
            int dot = torrentName.lastIndexOf('.');
            String dir = sCacheRoot + File.separator + (dot > 0 ? torrentName.substring(0, dot) : torrentName);

            long tid = helper.addTorrentTask(sTorrentFilePath, dir, index);
            if (tid < 0) return "ERR:创建下载任务失败";
            sTaskId = tid;

            for (int i = 0; i < 60; i++) {
                BtSubTaskDetail d = helper.getBtSubTaskInfo(tid, index);
                XLTaskInfo xi = d == null ? null : d.mTaskInfo;
                if (xi == null) {
                    Thread.sleep(1000);
                    continue;
                }
                if (xi.mTaskStatus == 3) {
                    return "ERR:下载失败 mErrorCode=" + xi.mErrorCode;
                }
                // 1=等待 2=完成 4=下载中 —— 都能起播
                if (xi.mTaskStatus == 1 || xi.mTaskStatus == 2 || xi.mTaskStatus == 4) {
                    String url = helper.getLoclUrl(dir + File.separator + target.mFileName);
                    if (!TextUtils.isEmpty(url)) {
                        Log.i(TAG, "迅雷起播：index=" + index + " status=" + xi.mTaskStatus
                                + " speed=" + xi.mDownloadSpeed + " url=" + url);
                        sLastPlayKey = playKey;
                        sLastPlayUrl = url;
                        return "OK:" + url;
                    }
                }
                Thread.sleep(1000);
            }
            return "ERR:解析下载超时";
        } catch (Throwable t) {
            return "ERR:" + rootCause(t);
        }
    }

    // ─────────────────────── ④ 停止 ───────────────────────

    public static synchronized void stop() {
        stopCurrent();
        // 显式停止后清掉播放缓存，避免下次拿到已失效的地址
        sLastPlayKey = null;
        sLastPlayUrl = null;
    }

    private static void stopCurrent() {
        if (sTaskId > 0) {
            try {
                XLTaskHelper.instance().stopTask(sTaskId);
            } catch (Throwable ignored) {
            }
            sTaskId = 0;
        }
    }

    /** 最近一次任务的实时信息：{@code "down|total|speed"}，取不到返回 {@code "0|0|0"} */
    public static synchronized String taskProgress() {
        if (!sInited || sTaskId <= 0) return "0|0|0";
        try {
            XLTaskInfo xi = XLTaskHelper.instance().getTaskInfo(sTaskId);
            if (xi == null) return "0|0|0";
            return xi.mDownloadSize + "|" + xi.mFileSize + "|" + xi.mDownloadSpeed;
        } catch (Throwable t) {
            return "0|0|0";
        }
    }

    // ─────────────────────── 工具 ───────────────────────

    private static String randomString(String base, int length) {
        Random r = new Random();
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < length; i++) {
            sb.append(base.charAt(r.nextInt(base.length())));
        }
        return sb.toString();
    }

    private static String rootCause(Throwable t) {
        Throwable r = t;
        while (r.getCause() != null) {
            r = r.getCause();
        }
        String m = r.getMessage();
        return r.getClass().getSimpleName() + (m == null ? "" : ": " + m);
    }

    /** 供 C# 侧拼装文件列表用（避免在 C# 里处理 Java 数组） */
    public static synchronized List<String> fileNames() {
        List<String> out = new ArrayList<>();
        if (sFiles != null) {
            for (TorrentFileInfo f : sFiles) {
                if (f != null) out.add(f.mFileName);
            }
        }
        return out;
    }
}
