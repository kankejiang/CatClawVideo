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

    public void setOnGenericMotionListener(Object l) { }

    public void setOnScrollChangeListener(Object l) { }

    public void setImportantForAccessibility(int mode) { }

    public void setContentDescription(CharSequence d) { }

    public void setFocusable(boolean f) { }

    public void setFocusableInTouchMode(boolean f) { }

    public boolean performClick() { return false; }

    public void bringToFront() { }


    // 监听器统一收 Object：spider 自带的匿名监听类未必实现本 stub 的同名接口，
    // 用 Object 兜住可避免 NoSuchMethodError（这些回调在 PC 上本就不会被触发）。
    public void setOnClickListener(Object l) { }
    public void setOnLongClickListener(Object l) { }
    public void setOnTouchListener(Object l) { }
    public void setOnKeyListener(Object l) { }
    public void setOnFocusChangeListener(Object l) { }

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
