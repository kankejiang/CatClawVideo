package android.content;

public interface DialogInterface {
    int BUTTON_POSITIVE = -1;
    int BUTTON_NEGATIVE = -2;
    int BUTTON_NEUTRAL = -3;

    void cancel();
    void dismiss();

    interface OnClickListener { void onClick(DialogInterface d, int which); }
    interface OnDismissListener { void onDismiss(DialogInterface d); }
    interface OnCancelListener { void onCancel(DialogInterface d); }
    interface OnShowListener { void onShow(DialogInterface d); }
    interface OnMultiChoiceClickListener { void onClick(DialogInterface d, int which, boolean isChecked); }
    interface OnKeyListener { boolean onKey(DialogInterface d, int keyCode, android.view.KeyEvent event); }
}
