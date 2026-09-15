package android.content.pm;

import android.os.Parcelable;
import java.util.Arrays;

/** Signature 桩：spider 里常用于取签名摘要做校验（这里按真实字节给稳定返回）。 */
public class Signature implements Parcelable {

    private final byte[] signature;

    public Signature(byte[] signature) { this.signature = signature == null ? new byte[0] : signature.clone(); }
    public Signature(String text) { this.signature = text == null ? new byte[0] : text.getBytes(); }

    public byte[] toByteArray() { return signature.clone(); }
    public String toCharsString() { return new String(signature); }
    public int getNumberOfSignatures() { return signature.length == 0 ? 0 : 1; }

    @Override public boolean equals(Object o) {
        return o instanceof Signature && Arrays.equals(signature, ((Signature) o).signature);
    }
    @Override public int hashCode() { return Arrays.hashCode(signature); }
    @Override public String toString() { return "Signature{" + toCharsString() + "}"; }
}
