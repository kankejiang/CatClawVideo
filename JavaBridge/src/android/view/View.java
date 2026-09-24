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

    public void setVisibility(int v) { }
    public int getVisibility() { return VISIBLE; }
    public void setEnabled(boolean e) { }
    public boolean isEnabled() { return true; }
    public void setLayoutParams(ViewGroup.LayoutParams params) { }
    public ViewGroup.LayoutParams getLayoutParams() { return new ViewGroup.LayoutParams(0, 0); }
    public Context getContext() { return android.app.Application.getInstance(); }

    public View findViewById(int id) { return null; }

    public View getRootView() { return this; }

    public ViewParent getParent() { return null; }

    public int getId() { return 0; }

    public void setId(int id) { }

    public int getWidth() { return 0; }

    public int getHeight() { return 0; }

    public void invalidate() { }

    public void requestLayout() { }

    public boolean post(Runnable action) { if (action != null) new Thread(action).start(); return true; }

    public boolean postDelayed(Runnable action, long delayMillis) { return true; }

    public android.os.Handler getHandler() { return new android.os.Handler(); }

    public void setTag(Object tag) { }

    public Object getTag() { return null; }

    public void setBackgroundColor(int color) { }

    public void setImportantForAccessibility(int mode) { }

    public void setContentDescription(CharSequence d) { }

    public void setFocusable(boolean f) { }

    public void setFocusableInTouchMode(boolean f) { }

    public boolean performClick() { return false; }

    public void bringToFront() { }

    public android.content.res.Resources getResources() { return new android.content.res.Resources(); }

    public android.view.ViewTreeObserver getViewTreeObserver() { return new android.view.ViewTreeObserver(); }

    public void setForeground(android.graphics.drawable.Drawable d) { }

    // ═══════════ 布局 / 外观 ═══════════
    // Guard 壳框架弹网盘对话框时会逐个设这些属性，**缺任意一个就 NoSuchMethodError**
    // 打断整条 UI 链（2026-09-24 实测：Pan.tF 卡在 View.setPadding 上）。
    public void setPadding(int left, int top, int right, int bottom) { }
    public void setPaddingRelative(int start, int top, int end, int bottom) { }
    public int getPaddingLeft() { return 0; }
    public int getPaddingTop() { return 0; }
    public int getPaddingRight() { return 0; }
    public int getPaddingBottom() { return 0; }
    public void setMinimumWidth(int minWidth) { }
    public void setMinimumHeight(int minHeight) { }
    public int getMinimumWidth() { return 0; }
    public int getMinimumHeight() { return 0; }
    public void setBackground(android.graphics.drawable.Drawable background) { }
    public void setBackgroundDrawable(android.graphics.drawable.Drawable background) { }
    public void setBackgroundResource(int resid) { }
    public android.graphics.drawable.Drawable getBackground() { return null; }
    public void setBackgroundTintList(android.content.res.ColorStateList tint) { }
    public void setAlpha(float alpha) { }
    public float getAlpha() { return 1f; }
    public void setElevation(float elevation) { }
    public void setTag(int key, Object tag) { }
    public Object getTag(int key) { return null; }
    public void setSelected(boolean selected) { }
    public boolean isSelected() { return false; }
    public void setClickable(boolean clickable) { }
    public boolean isClickable() { return true; }
    public int getMeasuredWidth() { return 0; }
    public int getMeasuredHeight() { return 0; }
    public void measure(int widthMeasureSpec, int heightMeasureSpec) { }
    public void layout(int l, int t, int r, int b) { }
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
    public void setOnClickListener(OnClickListener l) { }
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
}
