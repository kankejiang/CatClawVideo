package android.os;

/** Parcelable 桩（仅为让 spider 的 implements 子句能通过校验，不做任何序列化）。 */
public interface Parcelable {
    int CONTENTS_FILE_DESCRIPTOR = 1;
    int PARCELABLE_WRITE_RETURN_VALUE = 1;
}
