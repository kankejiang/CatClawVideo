package android.view;

import android.os.Parcelable;

/** WindowManager 桩：LayoutParams 常被 spider 用来做悬浮窗/自定义 View（PC 上不生效，但类型要齐）。 */
public class WindowManager {

    public static class LayoutParams extends ViewGroup.LayoutParams implements Parcelable {

        public static final int FLAG_FULLSCREEN = 0x00000400;
        public static final int FLAG_LAYOUT_IN_SCREEN = 0x00000100;
        public static final int FLAG_LAYOUT_NO_LIMITS = 0x00000200;
        public static final int FLAG_NOT_FOCUSABLE = 0x00000008;
        public static final int FLAG_NOT_TOUCHABLE = 0x00000010;
        public static final int FLAG_NOT_TOUCH_MODAL = 0x00000020;
        public static final int FLAG_DIM_BEHIND = 0x00000002;
        public static final int FLAG_SHOW_WHEN_LOCKED = 0x00080000;

        public static final int TYPE_APPLICATION = 2;
        public static final int TYPE_APPLICATION_OVERLAY = 2038;
        public static final int TYPE_PHONE = 2002;
        public static final int TYPE_SYSTEM_ALERT = 2003;

        public static final int FORMAT_TRANSLUCENT = -3;
        public static final int FORMAT_OPAQUE = -1;
        public static final int FORMAT_RGBA_8888 = 1;

        public static final int FIRST_SYSTEM_WINDOW = 2000;
        public static final int LAST_SYSTEM_WINDOW = 2999;

        public int x;
        public int y;
        public int type;
        public int flags;
        public int format;
        public int gravity;
        public float alpha = 1.0f;
        public float dimAmount = 1.0f;
        public float screenBrightness = -1.0f;
        public int softInputMode;
        public Object token;

        public LayoutParams() { super(MATCH_PARENT, MATCH_PARENT); }
        public LayoutParams(int w, int h) { super(w, h); }
        public LayoutParams(int w, int h, int type, int flags, int format) {
            super(w, h);
            this.type = type; this.flags = flags; this.format = format;
        }
        public LayoutParams(LayoutParams source) {
            super(source);
            if (source != null) {
                this.x = source.x; this.y = source.y; this.type = source.type; this.flags = source.flags;
                this.format = source.format; this.gravity = source.gravity; this.softInputMode = source.softInputMode;
            }
        }

        public void setTitle(CharSequence title) { }
        public int describeContents() { return 0; }
    }
}