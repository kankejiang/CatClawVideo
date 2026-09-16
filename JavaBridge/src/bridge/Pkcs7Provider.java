package bridge;

import java.security.AlgorithmParameters;
import java.security.InvalidAlgorithmParameterException;
import java.security.InvalidKeyException;
import java.security.Key;
import java.security.Provider;
import java.security.SecureRandom;
import java.security.Security;
import java.security.spec.AlgorithmParameterSpec;
import javax.crypto.BadPaddingException;
import javax.crypto.Cipher;
import javax.crypto.CipherSpi;
import javax.crypto.IllegalBlockSizeException;
import javax.crypto.ShortBufferException;

/**
 * 把 <code>AES/.../PKCS7Padding</code> 别名到标准 JVM 的 <code>PKCS5Padding</code>。
 *
 * <p><b>为什么需要</b>：Android 的 Conscrypt 同时提供 PKCS5Padding 与 PKCS7Padding，
 * 而标准 OpenJDK <b>只有 PKCS5Padding</b>。TVBox 系爬虫大量使用
 * <code>Cipher.getInstance("AES/CBC/PKCS7Padding")</code> 做接口加解密 →
 * 在桌面 JVM 上抛 <code>NoSuchAlgorithmException: Cannot find any provider supporting
 * AES/CBC/PKCS7Padding</code>，爬虫自己 catch 成 <code>aes decrypt fail</code>，
 * 整站数据取不到（实测 2026-09-16）。</p>
 *
 * <p>对 AES（块长固定 16 字节）而言 PKCS5Padding 与 PKCS7Padding <b>完全等价</b>，
 * 所以这里只做转发，不必实现真正的填充逻辑。零外部依赖（不引入 BouncyCastle）。</p>
 */
public final class Pkcs7Provider extends Provider {

    private static final String NAME = "CatClawPkcs7";

    public Pkcs7Provider() {
        super(NAME, 1.0, "alias AES PKCS7Padding -> PKCS5Padding");
        put("Cipher.AES/CBC/PKCS7Padding", AesCbcSpi.class.getName());
        put("Cipher.AES/ECB/PKCS7Padding", AesEcbSpi.class.getName());
        put("Cipher.AES/CTR/PKCS7Padding", AesCtrSpi.class.getName());
        put("Cipher.AES/GCM/PKCS7Padding", AesGcmSpi.class.getName());
    }

    /** 幂等安装（桥启动时调一次）。 */
    public static void install() {
        try {
            if (Security.getProvider(NAME) == null) Security.addProvider(new Pkcs7Provider());
        } catch (Throwable ignored) {
        }
    }

    public static class AesCbcSpi extends DelegatingSpi {
        public AesCbcSpi() { super("AES/CBC/PKCS5Padding"); }
    }

    public static class AesEcbSpi extends DelegatingSpi {
        public AesEcbSpi() { super("AES/ECB/PKCS5Padding"); }
    }

    public static class AesCtrSpi extends DelegatingSpi {
        public AesCtrSpi() { super("AES/CTR/NoPadding"); }
    }

    public static class AesGcmSpi extends DelegatingSpi {
        public AesGcmSpi() { super("AES/GCM/NoPadding"); }
    }

    /** 只做转发：真正的实现由默认 Provider 的 PKCS5Padding 提供。 */
    public abstract static class DelegatingSpi extends CipherSpi {

        private final String delegateAlgorithm;
        private Cipher delegate;

        protected DelegatingSpi(String delegateAlgorithm) {
            this.delegateAlgorithm = delegateAlgorithm;
        }

        private Cipher delegate() throws Exception {
            if (delegate == null) delegate = Cipher.getInstance(delegateAlgorithm);
            return delegate;
        }

        @Override protected void engineSetMode(String mode) { }

        @Override protected void engineSetPadding(String padding) { }

        @Override protected int engineGetBlockSize() {
            try { return delegate().getBlockSize(); } catch (Exception e) { return 16; }
        }

        @Override protected int engineGetOutputSize(int inputLen) {
            try { return delegate().getOutputSize(inputLen); } catch (Exception e) { return inputLen + 16; }
        }

        @Override protected byte[] engineGetIV() {
            try { return delegate().getIV(); } catch (Exception e) { return null; }
        }

        @Override protected AlgorithmParameters engineGetParameters() {
            try { return delegate().getParameters(); } catch (Exception e) { return null; }
        }

        @Override protected void engineInit(int opmode, Key key, SecureRandom random) throws InvalidKeyException {
            try { delegate().init(opmode, key, random); }
            catch (InvalidKeyException e) { throw e; }
            catch (Exception e) { throw new InvalidKeyException(String.valueOf(e.getMessage())); }
        }

        @Override protected void engineInit(int opmode, Key key, AlgorithmParameterSpec params, SecureRandom random)
                throws InvalidKeyException, InvalidAlgorithmParameterException {
            try { delegate().init(opmode, key, params, random); }
            catch (InvalidKeyException | InvalidAlgorithmParameterException e) { throw e; }
            catch (Exception e) { throw new InvalidKeyException(String.valueOf(e.getMessage())); }
        }

        @Override protected void engineInit(int opmode, Key key, AlgorithmParameters params, SecureRandom random)
                throws InvalidKeyException, InvalidAlgorithmParameterException {
            try { delegate().init(opmode, key, params, random); }
            catch (InvalidKeyException | InvalidAlgorithmParameterException e) { throw e; }
            catch (Exception e) { throw new InvalidKeyException(String.valueOf(e.getMessage())); }
        }

        @Override protected byte[] engineUpdate(byte[] input, int inputOffset, int inputLen) {
            try { return delegate().update(input, inputOffset, inputLen); } catch (Exception e) { return null; }
        }

        @Override protected int engineUpdate(byte[] input, int inputOffset, int inputLen, byte[] output, int outputOffset)
                throws ShortBufferException {
            try { return delegate().update(input, inputOffset, inputLen, output, outputOffset); }
            catch (ShortBufferException e) { throw e; }
            catch (Exception e) { return 0; }
        }

        @Override protected byte[] engineDoFinal(byte[] input, int inputOffset, int inputLen)
                throws IllegalBlockSizeException, BadPaddingException {
            try { return delegate().doFinal(input, inputOffset, inputLen); }
            catch (IllegalBlockSizeException | BadPaddingException e) { throw e; }
            catch (Exception e) { throw new IllegalBlockSizeException(String.valueOf(e.getMessage())); }
        }

        @Override protected int engineDoFinal(byte[] input, int inputOffset, int inputLen, byte[] output, int outputOffset)
                throws ShortBufferException, IllegalBlockSizeException, BadPaddingException {
            try { return delegate().doFinal(input, inputOffset, inputLen, output, outputOffset); }
            catch (ShortBufferException | IllegalBlockSizeException | BadPaddingException e) { throw e; }
            catch (Exception e) { throw new IllegalBlockSizeException(String.valueOf(e.getMessage())); }
        }
    }
}
