package android.view;

import android.content.Context;
import android.util.AttributeSet;

public class View {

    // ── 常用常量（spider 里常按名引用，缺了会 NoSuchFieldError）──
    public static final int VISIBLE = 0;
    public static final int INVISIBLE = 4;
    public static final int GONE = 8;

    public View() { }
    public View(Context c) { }
    public View(Context c, AttributeSet attrs) { }

    public void setVisibility(int v) { visFlag = v; }
    public int getVisibility() { return visFlag; }
    public void setEnabled(boolean e) { enFlag = e; }
    public boolean isEnabled() { return enFlag; }
    /** LayoutParams 真存（A 路线树序列化要读宽高语义）；jar 没设过时回落旧行为（不落盘）。 */
    public void setLayoutParams(ViewGroup.LayoutParams params) { layoutParams = params; }
    public ViewGroup.LayoutParams getLayoutParams() {
        return layoutParams != null ? layoutParams : new ViewGroup.LayoutParams(0, 0);
    }
    public Context getContext() { return android.app.Application.getInstance(); }

    public View findViewById(int id) { return null; }

    public View getRootView() { return this; }

    public ViewParent getParent() { return null; }

    public int getId() { return 0; }

    public void setId(int id) { }

    // ── 尺寸（2026-09-30 扫码链路）：以前恒返 0，壳的自定义 View（zxing 的 QRCodeView 一族）
    //    在 onDraw 里用 getWidth()/canvas.getWidth() 算模块边距 ⇒ 算出 0 就什么都画不出来。
    //    现在 measure/layout 真记录尺寸，配合 Canvas 的录制实现才能把码画进位图。
    private int measuredW, measuredH, viewW, viewH;

    public int getWidth() { return viewW != 0 ? viewW : measuredW; }

    public int getHeight() { return viewH != 0 ? viewH : measuredH; }

    /**
     * 桌面没有真正的重绘管线，但**不能当空操作**：壳的扫码二维码常是「先弹框 → 异步取到登录
     * URL → invalidate()」的模型，任务落在这里就被丢掉，宿主永远只收到一个没有码的框
     * （2026-10-01 实测：驱动 onDraw 两轮都是 着色像素=0/230400）。
     * 这里把「某个 View 要重画」上报给当前对话框，由它重新取码并按 seq 补发 ui-qr。
     */
    public void invalidate() { android.app.Dialog.noteInvalidate(this); }

    /** 局部失效重载：桌面不画脏区，一律按「整个 View 要重画」上报。 */
    public void invalidate(int l, int t, int r, int b) { invalidate(); }

    public void invalidate(android.graphics.Rect dirty) { invalidate(); }

    public void postInvalidate() { invalidate(); }

    public void postInvalidateDelayed(long delayMillis) {
        android.os.Handler.schedule(this::invalidate, Math.max(0L, delayMillis));
    }

    public void requestLayout() { }

    public boolean post(Runnable action) { return postDelayed(action, 0L); }

    /**
     * ⚠ 必须真的延迟执行：爬虫用它做退避重试与"稍后再看状态"，
     * 旧实现 {@code return true} 丢掉任务（同 {@link android.os.Handler#postDelayed}）。
     */
    public boolean postDelayed(Runnable action, long delayMillis) {
        if (action == null) return false;
        android.os.Handler.schedule(action, delayMillis);
        return true;
    }

    /** 真实存在被调用（{@code ProxyOrigin} 用它撤弹幕/轮询的重试），缺了就是 NoSuchMethodError。 */
    public boolean removeCallbacks(Runnable action) {
        if (action == null) return false;
        android.os.Handler.cancel(action);
        return true;
    }

    public android.os.Handler getHandler() { return new android.os.Handler(); }

    /**
     * ⚠ tag 必须真的存下来：jar 用它携带业务索引（网盘对话框给每个按钮
     * {@code setTag(第几家盘)}，点击时 {@code ((Integer) v.getTag()).intValue()} 取回）。
     * 早先这里是空实现 → getTag() 恒 null → 点击监听器一进来就 NPE，
     * 宿主表现为「点了没反应、二维码不弹」（2026-09-24 实测栈：Pan.SN ← merge.Bu.onClick）。
     */
    private Object tag;

    public void setTag(Object t) { tag = t; }

    public Object getTag() { return tag; }

    public void setBackgroundColor(int color) { bgColor = color; bgSet = true; }

    public void setImportantForAccessibility(int mode) { }

    public void setContentDescription(CharSequence d) { }

    public void setFocusable(boolean f) { }

    public void setFocusableInTouchMode(boolean f) { }

    /** Android 语义：触发本节点的 OnClickListener（jar 会自己调它模拟点击）。 */
    public boolean performClick() {
        if (clickListener == null) return false;
        try { clickListener.onClick(this); } catch (Throwable ignored) { }
        return true;
    }

    public void bringToFront() { }

    public android.content.res.Resources getResources() { return new android.content.res.Resources(); }

    public android.view.ViewTreeObserver getViewTreeObserver() { return new android.view.ViewTreeObserver(); }

    public void setForeground(android.graphics.drawable.Drawable d) { }

    // ═══════════ 布局 / 外观 ═══════════
    // Guard 壳框架弹网盘对话框时会逐个设这些属性，**缺任意一个就 NoSuchMethodError**
    // 打断整条 UI 链（2026-09-24 实测：Pan.tF 卡在 View.setPadding 上）。
    // A 路线（2026-10-02）：padding/背景/布局位置真存——整树序列化要靠它们还原 jar 的界面。
    public void setPadding(int left, int top, int right, int bottom) {
        padL = left; padT = top; padR = right; padB = bottom;
    }
    public void setPaddingRelative(int start, int top, int end, int bottom) {
        setPadding(start, top, end, bottom);
    }
    public int getPaddingLeft() { return padL; }
    public int getPaddingTop() { return padT; }
    public int getPaddingRight() { return padR; }
    public int getPaddingBottom() { return padB; }
    public void setMinimumWidth(int minWidth) { }
    public void setMinimumHeight(int minHeight) { }
    public int getMinimumWidth() { return 0; }
    public int getMinimumHeight() { return 0; }
    public void setBackground(android.graphics.drawable.Drawable background) { bgDrawable = background; }
    public void setBackgroundDrawable(android.graphics.drawable.Drawable background) { bgDrawable = background; }
    public void setBackgroundResource(int resid) { }
    public android.graphics.drawable.Drawable getBackground() { return bgDrawable; }
    public void setBackgroundTintList(android.content.res.ColorStateList tint) { }
    public void setAlpha(float alpha) { }
    public float getAlpha() { return 1f; }
    public void setElevation(float elevation) { }
    /** 带 key 的 tag（Android 用资源 id 区分多个附加数据），同样必须真存。 */
    private java.util.Map<Integer, Object> tagged;

    public void setTag(int key, Object t) {
        if (tagged == null) tagged = new java.util.HashMap<>();
        tagged.put(key, t);
    }

    public Object getTag(int key) { return tagged == null ? null : tagged.get(key); }
    public void setSelected(boolean selected) { selFlag = selected; }
    public boolean isSelected() { return selFlag; }
    public void setClickable(boolean clickable) { clickFlag = clickable; }
    public boolean isClickable() { return clickSet ? clickFlag : true; }
    public int getMeasuredWidth() { return measuredW; }
    public int getMeasuredHeight() { return measuredH; }

    /**
     * 真跑一遍 {@code onMeasure}（壳的二维码 View 在这里定尺寸），并把结果记进
     * {@code measuredW/H}。以前是空实现 ⇒ 自定义 View 永远量出 0x0，
     * {@code onDraw} 里按宽高算模块边距就什么都画不出来。
     */
    public void measure(int widthMeasureSpec, int heightMeasureSpec) {
        try {
            onMeasure(widthMeasureSpec, heightMeasureSpec);
        } catch (Throwable t) {
            System.err.println("[ui] onMeasure 失败 " + getClass().getSimpleName() + ": " + t);
        }
        if (measuredW == 0) measuredW = MeasureSpec.getSize(widthMeasureSpec);
        if (measuredH == 0) measuredH = MeasureSpec.getSize(heightMeasureSpec);
    }

    /** 子类（zxing 的 QRCodeView 等）会覆写并调 {@link #setMeasuredDimension}。 */
    public void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        setMeasuredDimension(getDefaultSize(MeasureSpec.getSize(widthMeasureSpec), suggestedMinimumWidth()),
                getDefaultSize(MeasureSpec.getSize(heightMeasureSpec), suggestedMinimumHeight()));
    }

    protected int suggestedMinimumWidth() { return 0; }
    protected int suggestedMinimumHeight() { return 0; }

    public static int getDefaultSize(int size, int minSize) { return size > minSize ? size : minSize; }

    public void layout(int l, int t, int r, int b) {
        viewW = Math.max(0, r - l);
        viewH = Math.max(0, b - t);
        uiLeft = l; uiTop = t;
    }

    /** 真机的 {@code View.MeasureSpec}：壳的 onMeasure 普遍用 {@code getSize}/{@code getMode}。 */
    public static class MeasureSpec {
        public static final int UNSPECIFIED = 0;
        public static final int EXACTLY = 1 << 30;
        public static final int AT_MOST = 2 << 30;

        public static int makeMeasureSpec(int size, int mode) {
            return (size & 0x3FFFFFFF) | (mode & (3 << 30));
        }

        public static int getMode(int spec) { return spec & (3 << 30); }

        public static int getSize(int spec) { return spec & 0x3FFFFFFF; }
    }

    public boolean isShown() { return true; }
    public boolean requestFocus() { return true; }
    public void clearFocus() { }
    public void setOnDragListener(Object l) { }
    public void setSoundEffectsEnabled(boolean enabled) { }
    public void setScrollBarStyle(int style) { }
    public void setVerticalScrollBarEnabled(boolean v) { }
    public void setHorizontalScrollBarEnabled(boolean v) { }
    public void setOverScrollMode(int mode) { }
    public void setFitsSystemWindows(boolean fit) { }
    public void setSystemUiVisibility(int visibility) { }
    public void setTransitionName(String name) { }
    public void setRotation(float r) { }
    public void setRotationX(float r) { }
    public void setRotationY(float r) { }
    public void setPivotX(float x) { }
    public void setPivotY(float y) { }
    public void setScaleX(float s) { }
    public void setScaleY(float s) { }
    public void setTranslationX(float x) { }
    public void setTranslationY(float y) { }
    public void setTranslationZ(float z) { }
    public void setX(float x) { }
    public void setY(float y) { }
    public float getX() { return 0f; }
    public float getY() { return 0f; }
    public void setCameraDistance(float d) { }
    public void setLayerType(int layerType, android.graphics.Paint paint) { }
    public void setWillNotDraw(boolean willNotDraw) { }
    public void setDrawingCacheEnabled(boolean enabled) { }
    public void setVerticalFadingEdgeEnabled(boolean v) { }
    public void setOutlineProvider(Object provider) { }
    public void setClipToOutline(boolean clip) { }
    public void setStateListAnimator(Object animator) { }


    // ⚠ 这里必须用**精确的嵌套接口类型**，不能用 Object 兜底：
    //   jar 是预编译的，调用点描述符写死 `(Landroid/view/View$OnFocusChangeListener;)V`，
    //   用 Object 声明只会把 NoSuchMethodError 留到运行时（2026-09-24 实测 Pan.tF 踩到）。
    //   我们的嵌套接口方法签名与真实 Android 一致，jar 的匿名监听类能正常赋值进来。
    public void setOnClickListener(OnClickListener l) { clickListener = l; }
    private OnClickListener clickListener;

    /**
     * jar 挂在节点上的点击监听。
     * <para>宿主摊平 {@code setView} 的自定义 View 树时按它决定「哪一行可点」，
     * 用户点击再回过来触发它——网盘「已登录+启用中」列表就是这么工作的。
     * 早先这里是纯 no-op，监听器直接丢弃，所以对话框永远只有个空壳。</para>
     */
    public OnClickListener clickListener() { return clickListener; }

    public void setOnLongClickListener(OnLongClickListener l) { }
    public void setOnTouchListener(OnTouchListener l) { }
    public void setOnKeyListener(OnKeyListener l) { }
    public void setOnFocusChangeListener(OnFocusChangeListener l) { }
    public void setOnScrollChangeListener(OnScrollChangeListener l) { }

    // 下面这个没有对应的嵌套接口桩，保持 Object（可用性优先）
    public void setOnGenericMotionListener(Object l) { }

    // ── 内部接口：spider 常以匿名类 implements View.OnXxxListener，
    //    类加载/校验阶段若缺这些类型会直接 ClassNotFoundException ──
    public interface OnClickListener { void onClick(View v); }
    public interface OnLongClickListener { boolean onLongClick(View v); }
    public interface OnTouchListener { boolean onTouch(View v, MotionEvent event); }
    public interface OnKeyListener { boolean onKey(View v, int keyCode, KeyEvent event); }
    public interface OnFocusChangeListener { void onFocusChange(View v, boolean hasFocus); }
    public interface OnScrollChangeListener {
        void onScrollChange(View v, int scrollX, int scrollY, int oldScrollX, int oldScrollY);
    }

    // ── 自动补齐（第 2 批）：jar 引用到但桩缺失的成员 ──
    public boolean performLongClick() { return false; }
    public boolean hasFocus() { return false; }
    public boolean isFocused() { return false; }
    public android.view.View findFocus() { return null; }
    public void setNextFocusLeftId(int p0) { }
    public void setNextFocusRightId(int p0) { }
    public void setNextFocusUpId(int p0) { }
    public void setNextFocusDownId(int p0) { }
    public void setFocusable(int p0) { }
    public boolean isAttachedToWindow() { return false; }
    public void setLongClickable(boolean p0) { }
    public boolean isFocusable() { return false; }
    public android.view.View focusSearch(int p0) { return null; }
    public java.util.ArrayList getFocusables(int p0) { return null; }
    public void addFocusables(java.util.ArrayList p0, int p1) { }
    public boolean isInTouchMode() { return false; }
    public int getTop() { return 0; }
    public int getLeft() { return 0; }
    public boolean getGlobalVisibleRect(android.graphics.Rect p0) { return false; }
    public void onDraw(android.graphics.Canvas p0) { }

    /** 真机的 {@code View.draw(Canvas)} 会走到 {@code onDraw}；桩给同样的骨架
     *  （壳自己 new Canvas(bmp) 再 draw 时要用）。⚠ 桩不再反向驱动这棵树离屏重绘：
     *  替订阅源画 UI 这条路 2026-10-01 已整删，见 {@code graphics/Canvas} 的类注释。 */
    public void draw(android.graphics.Canvas p0) { onDraw(p0); }

    /** 记录子类量的尺寸（以前是空实现 ⇒ 自定义 View 永远 0x0）。 */
    public void setMeasuredDimension(int p0, int p1) { measuredW = p0; measuredH = p1; }
    public android.os.IBinder getWindowToken() { return null; }
    public int getSystemUiVisibility() { return 0; }
    public static int generateViewId() { return 0; }
    /**
     * ⚠️ 绝不能返回 null（2026-10-01 实测根因）：Guard 系壳登录后会走
     * {@code view.animate().alpha(..)} 这类链式动画，null 直接抛
     * {@code NullPointerException: Attempt to invoke virtual method
     * 'ViewPropertyAnimator ViewPropertyAnimator.alpha(float)' on a null object reference}，
     * 把壳的 {@code [Init] async task} 整个打死 —— 表现就是「扫码成功、却永远卡在
     * 『正在获取账号信息…』、登录态也存不下来」。返回单例桩即可：{@link ViewPropertyAnimator}
     * 的每个方法都是 no-op 且 {@code return this}，链式调用天然安全。
     */
    public android.view.ViewPropertyAnimator animate() { return ANIMATOR; }

    private static final android.view.ViewPropertyAnimator ANIMATOR = new android.view.ViewPropertyAnimator();

    // ═══════════ A 路线：整树序列化用的真实属性存储（2026-10-02）═══════════
    // jar 设置的视觉信息以前全被空 setter 丢掉（宿主只能按自己主题重画，见 2026-10-02
    // 用户对比截图「夸克网盘登录框」）。这里真存 padding/背景/位置/状态，供
    // Dialog.flattenView 序列化整棵树。ui* 前缀 = 专给序列化器的访问器，不与真机签名冲突。
    private int visFlag = VISIBLE;
    private boolean enFlag = true;
    private boolean selFlag;
    private boolean clickSet; private boolean clickFlag;
    private int padL, padT, padR, padB;
    private int uiLeft, uiTop;
    private int bgColor; private boolean bgSet;
    private android.graphics.drawable.Drawable bgDrawable;
    private ViewGroup.LayoutParams layoutParams;

    /** 背景Drawable（ColorDrawable/GradientDrawable 时序列化色值/圆角）。 */
    public android.graphics.drawable.Drawable uiBgDrawable() { return bgDrawable; }
    /** setBackgroundColor 设置的纯色背景（与 uiBgDrawable 二选一，bgSet 才有效）。 */
    public int uiBgColor() { return bgColor; }
    public boolean uiBgColorSet() { return bgSet; }
    public int uiVisibility() { return visFlag; }
    public boolean uiEnabled() { return enFlag; }
    public boolean uiSelected() { return selFlag; }
    public int uiPadL() { return padL; }
    public int uiPadT() { return padT; }
    public int uiPadR() { return padR; }
    public int uiPadB() { return padB; }
    /** layout() 的左/上坐标（jar 自定义 View 自己 layout 过才有值，多数节点是 0）。 */
    public int uiLeft() { return uiLeft; }
    public int uiTop() { return uiTop; }
    public ViewGroup.LayoutParams uiLayoutParams() { return layoutParams; }
}
