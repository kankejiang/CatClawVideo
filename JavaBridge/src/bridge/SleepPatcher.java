package bridge;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * 类加载期字节码变换：把 spider jar 里的 {@code Thread.sleep(J)V} 重定向到
 * {@code android.os.SystemClock.sleep(J)V}。
 *
 * <p><b>为什么</b>：Guard 系网盘 jar 的扫码登录轮询按<b>次数</b>计重试预算（实测约 13 次、
 * 每次节拍 1 秒 ≈ 13 秒窗口），手机扫码授权普遍超时——「扫了码也没用」（2026-09-25 mitm
 * 实测定位：token/出码/轮询/换票请求全部正确，唯独窗口太短）。节拍伸缩的开关在
 * {@link android.os.SystemClock}（二维码框存续期间 ×20），但部分 jar 构建的轮询循环直接
 * 走 {@code Thread.sleep}，绕开了桩——加载期重定向后全部节拍统一走可伸缩的桩。</p>
 *
 * <p><b>安全性</b>：两者语义等价（可中断的毫秒睡眠；差异仅在 InterruptedException 的
 * 传播方式——桩吞掉并复原中断位，轮询退避场景无影响）。窗口之外伸缩系数为 1，行为与
 * 原生 Thread.sleep 完全一致。变换只替换 invokestatic 的方法引用（描述符相同、栈形状
 * 不变），其余字节码原样保留；变换失败时按原字节码加载（仅失去伸缩能力，不出错）。</p>
 */
public final class SleepPatcher {

    private SleepPatcher() { }

    public static byte[] patch(byte[] bytes) {
        try {
            ClassReader cr = new ClassReader(bytes);
            ClassWriter cw = new ClassWriter(cr, 0);
            final String[] cls = {null};
            cr.accept(new ClassVisitor(Opcodes.ASM9, cw) {
                @Override
                public void visit(int version, int access, String name, String signature,
                                  String superName, String[] interfaces) {
                    cls[0] = name;
                    super.visit(version, access, name, signature, superName, interfaces);
                }

                @Override
                public MethodVisitor visitMethod(int access, String name, String desc,
                                                 String signature, String[] exceptions) {
                    MethodVisitor mv = super.visitMethod(access, name, desc, signature, exceptions);
                    return new MethodVisitor(Opcodes.ASM9, mv) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String name2,
                                                    String desc2, boolean isInterface) {
                            // Thread.sleep(J)V → SystemClock.sleep(J)V（同形静态）
                            if (opcode == Opcodes.INVOKESTATIC
                                    && "java/lang/Thread".equals(owner)
                                    && "sleep".equals(name2)
                                    && "(J)V".equals(desc2)) {
                                System.err.println("[loader] sleep 重定向: " + cls[0] + "." + name);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC,
                                        "android/os/SystemClock", "sleep", "(J)V", false);
                                return;
                            }
                            // TimeUnit.sleep(J)V → shim（内部 parkNanos，只能拦调用点）
                            if (opcode == Opcodes.INVOKEVIRTUAL
                                    && "java/util/concurrent/TimeUnit".equals(owner)
                                    && "sleep".equals(name2)
                                    && "(J)V".equals(desc2)) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC,
                                        "bridge/SlowShims", "sleep",
                                        "(Ljava/util/concurrent/TimeUnit;J)V", false);
                                return;
                            }
                            // Object.wait(J)V / (JI)V → shim（轮询循环用监视器等待的场景）
                            if (opcode == Opcodes.INVOKEVIRTUAL
                                    && "java/lang/Object".equals(owner)
                                    && "wait".equals(name2)
                                    && "(J)V".equals(desc2)) {
                                System.err.println("[loader] wait 重定向: " + cls[0] + "." + name);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC,
                                        "bridge/SlowShims", "wait",
                                        "(Ljava/lang/Object;J)V", false);
                                return;
                            }
                            if (opcode == Opcodes.INVOKEVIRTUAL
                                    && "java/lang/Object".equals(owner)
                                    && "wait".equals(name2)
                                    && "(JI)V".equals(desc2)) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC,
                                        "bridge/SlowShims", "wait",
                                        "(Ljava/lang/Object;JI)V", false);
                                return;
                            }
                            // ScheduledExecutorService.schedule* / Timer.schedule* → shim（接收者作首参）
                            String shim = null;
                            if (opcode == Opcodes.INVOKEINTERFACE
                                    && "java/util/concurrent/ScheduledExecutorService".equals(owner)
                                    && ("schedule".equals(name2) || "scheduleAtFixedRate".equals(name2)
                                        || "scheduleWithFixedDelay".equals(name2))) {
                                shim = "java/util/concurrent/ScheduledExecutorService";
                            } else if (opcode == Opcodes.INVOKEVIRTUAL
                                    && "java/util/Timer".equals(owner)
                                    && ("schedule".equals(name2) || "scheduleAtFixedRate".equals(name2))) {
                                shim = "java/util/Timer";
                            }
                            if (shim != null) {
                                System.err.println("[loader] 延迟重定向: " + cls[0] + "." + name + " → " + name2);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC,
                                        "bridge/SlowShims", name2,
                                        "(L" + shim + ";" + desc2.substring(1), false);
                                return;
                            }
                            super.visitMethodInsn(opcode, owner, name2, desc2, isInterface);
                        }
                    };
                }
            }, 0);
            return cw.toByteArray();
        } catch (Throwable t) {
            System.err.println("[loader] sleep 变换失败，按原字节码加载: " + t);
            return bytes;
        }
    }
}
