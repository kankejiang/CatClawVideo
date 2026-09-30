package android.text;

/** android.text.InputFilter 桩（TextView.setFilters 签名引用）。 */
public interface InputFilter {
    CharSequence filter(CharSequence source, int start, int end, android.text.Spanned dest, int dstart, int dend);
}
