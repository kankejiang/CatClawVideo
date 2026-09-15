package android.util;

/** XML 属性集（View(Context, AttributeSet) 构造链需要）。 */
public interface AttributeSet {
    int getAttributeCount();
    String getAttributeName(int index);
    String getAttributeValue(int index);
    String getAttributeValue(String namespace, String name);
    int getAttributeIntValue(String namespace, String name, int defaultValue);
    boolean getAttributeBooleanValue(String namespace, String name, boolean defaultValue);
}
