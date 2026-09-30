package android.widget;

/** android.widget.Scroller 桩。 */
public class Scroller {
    public Scroller(android.content.Context context) { }
    public void startScroll(int startX, int startY, int dx, int dy) { }
    public void startScroll(int startX, int startY, int dx, int dy, int duration) { }
    public boolean computeScrollOffset() { return false; }
    public int getCurrX() { return 0; }
    public int getCurrY() { return 0; }
    public void abortAnimation() { }
    public void forceFinished(boolean finished) { }
}
