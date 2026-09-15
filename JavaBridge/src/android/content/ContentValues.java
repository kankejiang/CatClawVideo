package android.content;

import android.os.Parcelable;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/** ContentValues 桩：内存实现（spider 常用来组装插入/更新字段）。 */
public final class ContentValues implements Parcelable {

    public static final String TAG = "ContentValues";

    private final Map<String, Object> values = new LinkedHashMap<>();

    public ContentValues() { }
    public ContentValues(int size) { }
    public ContentValues(ContentValues from) { if (from != null) values.putAll(from.values); }

    public void put(String key, String value) { values.put(key, value); }
    public void put(String key, Byte value) { values.put(key, value); }
    public void put(String key, Short value) { values.put(key, value); }
    public void put(String key, Integer value) { values.put(key, value); }
    public void put(String key, Long value) { values.put(key, value); }
    public void put(String key, Float value) { values.put(key, value); }
    public void put(String key, Double value) { values.put(key, value); }
    public void put(String key, Boolean value) { values.put(key, value); }
    public void put(String key, byte[] value) { values.put(key, value); }
    public void putNull(String key) { values.put(key, null); }
    public void putAll(ContentValues other) { if (other != null) values.putAll(other.values); }

    public String getAsString(String key) { Object v = values.get(key); return v == null ? null : String.valueOf(v); }
    public Long getAsLong(String key) { Object v = values.get(key); return v instanceof Number ? ((Number) v).longValue() : null; }
    public Integer getAsInteger(String key) { Object v = values.get(key); return v instanceof Number ? ((Number) v).intValue() : null; }
    public Boolean getAsBoolean(String key) { Object v = values.get(key); return v instanceof Boolean ? (Boolean) v : null; }
    public Double getAsDouble(String key) { Object v = values.get(key); return v instanceof Number ? ((Number) v).doubleValue() : null; }
    public Float getAsFloat(String key) { Object v = values.get(key); return v instanceof Number ? ((Number) v).floatValue() : null; }
    public byte[] getAsByteArray(String key) { Object v = values.get(key); return v instanceof byte[] ? (byte[]) v : null; }
    public Object get(String key) { return values.get(key); }

    public void remove(String key) { values.remove(key); }
    public void clear() { values.clear(); }
    public int size() { return values.size(); }
    public Set<String> keySet() { return values.keySet(); }
    public Set<Map.Entry<String, Object>> valueSet() { return values.entrySet(); }
    public boolean containsKey(String key) { return values.containsKey(key); }
    public boolean isEmpty() { return values.isEmpty(); }
}
