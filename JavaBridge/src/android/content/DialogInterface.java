package android.content;
public interface DialogInterface {
    int BUTTON_POSITIVE = -1;
    int BUTTON_NEGATIVE = -2;
    interface OnClickListener { void onClick(DialogInterface d, int which); }
}
