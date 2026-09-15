package android.content;

/** ActivityNotFoundException 桩（真实继承 RuntimeException，保持同一层级以免 catch 失配）。 */
public class ActivityNotFoundException extends RuntimeException {
    public ActivityNotFoundException() { super(); }
    public ActivityNotFoundException(String name) { super(name); }
}
