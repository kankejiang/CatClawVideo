package android.text;

/** android.text.TextWatcher 桩（TextView.addTextChangedListener 签名引用）。 */
public interface TextWatcher {
    void beforeTextChanged(CharSequence s, int start, int count, int after);
    void onTextChanged(CharSequence s, int start, int before, int count);
    void afterTextChanged(android.text.Editable s);
}
