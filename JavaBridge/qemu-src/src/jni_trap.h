// jni_trap.h —— 由 tools/gen_jni_trap.py 从 NDK jni.h 自动生成（233 槽），勿手改。
// 陷阱：引擎调到未实现的 JNI 槽位时打印槽位名并返回 0（避免裸 NULL 跳转崩溃）。
#include <stdio.h>

#define JNI_SLOT_COUNT 233

static const char *g_trap_names[JNI_SLOT_COUNT] = {
    "reserved0",
    "reserved1",
    "reserved2",
    "reserved3",
    "GetVersion",
    "DefineClass",
    "FindClass",
    "FromReflectedMethod",
    "FromReflectedField",
    "ToReflectedMethod",
    "GetSuperclass",
    "IsAssignableFrom",
    "ToReflectedField",
    "Throw",
    "ThrowNew",
    "ExceptionOccurred",
    "ExceptionDescribe",
    "ExceptionClear",
    "FatalError",
    "PushLocalFrame",
    "PopLocalFrame",
    "NewGlobalRef",
    "DeleteGlobalRef",
    "DeleteLocalRef",
    "IsSameObject",
    "NewLocalRef",
    "EnsureLocalCapacity",
    "AllocObject",
    "NewObject",
    "NewObjectV",
    "NewObjectA",
    "GetObjectClass",
    "IsInstanceOf",
    "GetMethodID",
    "CallObjectMethod",
    "CallObjectMethodV",
    "CallObjectMethodA",
    "CallBooleanMethod",
    "CallBooleanMethodV",
    "CallBooleanMethodA",
    "CallByteMethod",
    "CallByteMethodV",
    "CallByteMethodA",
    "CallCharMethod",
    "CallCharMethodV",
    "CallCharMethodA",
    "CallShortMethod",
    "CallShortMethodV",
    "CallShortMethodA",
    "CallIntMethod",
    "CallIntMethodV",
    "CallIntMethodA",
    "CallLongMethod",
    "CallLongMethodV",
    "CallLongMethodA",
    "CallFloatMethod",
    "CallFloatMethodV",
    "CallFloatMethodA",
    "CallDoubleMethod",
    "CallDoubleMethodV",
    "CallDoubleMethodA",
    "CallVoidMethod",
    "CallVoidMethodV",
    "CallVoidMethodA",
    "CallNonvirtualObjectMethod",
    "CallNonvirtualObjectMethodV",
    "CallNonvirtualObjectMethodA",
    "CallNonvirtualBooleanMethod",
    "CallNonvirtualBooleanMethodV",
    "CallNonvirtualBooleanMethodA",
    "CallNonvirtualByteMethod",
    "CallNonvirtualByteMethodV",
    "CallNonvirtualByteMethodA",
    "CallNonvirtualCharMethod",
    "CallNonvirtualCharMethodV",
    "CallNonvirtualCharMethodA",
    "CallNonvirtualShortMethod",
    "CallNonvirtualShortMethodV",
    "CallNonvirtualShortMethodA",
    "CallNonvirtualIntMethod",
    "CallNonvirtualIntMethodV",
    "CallNonvirtualIntMethodA",
    "CallNonvirtualLongMethod",
    "CallNonvirtualLongMethodV",
    "CallNonvirtualLongMethodA",
    "CallNonvirtualFloatMethod",
    "CallNonvirtualFloatMethodV",
    "CallNonvirtualFloatMethodA",
    "CallNonvirtualDoubleMethod",
    "CallNonvirtualDoubleMethodV",
    "CallNonvirtualDoubleMethodA",
    "CallNonvirtualVoidMethod",
    "CallNonvirtualVoidMethodV",
    "CallNonvirtualVoidMethodA",
    "GetFieldID",
    "GetObjectField",
    "GetBooleanField",
    "GetByteField",
    "GetCharField",
    "GetShortField",
    "GetIntField",
    "GetLongField",
    "GetFloatField",
    "GetDoubleField",
    "SetObjectField",
    "SetBooleanField",
    "SetByteField",
    "SetCharField",
    "SetShortField",
    "SetIntField",
    "SetLongField",
    "SetFloatField",
    "SetDoubleField",
    "GetStaticMethodID",
    "CallStaticObjectMethod",
    "CallStaticObjectMethodV",
    "CallStaticObjectMethodA",
    "CallStaticBooleanMethod",
    "CallStaticBooleanMethodV",
    "CallStaticBooleanMethodA",
    "CallStaticByteMethod",
    "CallStaticByteMethodV",
    "CallStaticByteMethodA",
    "CallStaticCharMethod",
    "CallStaticCharMethodV",
    "CallStaticCharMethodA",
    "CallStaticShortMethod",
    "CallStaticShortMethodV",
    "CallStaticShortMethodA",
    "CallStaticIntMethod",
    "CallStaticIntMethodV",
    "CallStaticIntMethodA",
    "CallStaticLongMethod",
    "CallStaticLongMethodV",
    "CallStaticLongMethodA",
    "CallStaticFloatMethod",
    "CallStaticFloatMethodV",
    "CallStaticFloatMethodA",
    "CallStaticDoubleMethod",
    "CallStaticDoubleMethodV",
    "CallStaticDoubleMethodA",
    "CallStaticVoidMethod",
    "CallStaticVoidMethodV",
    "CallStaticVoidMethodA",
    "GetStaticFieldID",
    "GetStaticObjectField",
    "GetStaticBooleanField",
    "GetStaticByteField",
    "GetStaticCharField",
    "GetStaticShortField",
    "GetStaticIntField",
    "GetStaticLongField",
    "GetStaticFloatField",
    "GetStaticDoubleField",
    "SetStaticObjectField",
    "SetStaticBooleanField",
    "SetStaticByteField",
    "SetStaticCharField",
    "SetStaticShortField",
    "SetStaticIntField",
    "SetStaticLongField",
    "SetStaticFloatField",
    "SetStaticDoubleField",
    "NewString",
    "GetStringLength",
    "GetStringChars",
    "ReleaseStringChars",
    "NewStringUTF",
    "GetStringUTFLength",
    "GetStringUTFChars",
    "ReleaseStringUTFChars",
    "GetArrayLength",
    "NewObjectArray",
    "GetObjectArrayElement",
    "SetObjectArrayElement",
    "NewBooleanArray",
    "NewByteArray",
    "NewCharArray",
    "NewShortArray",
    "NewIntArray",
    "NewLongArray",
    "NewFloatArray",
    "NewDoubleArray",
    "GetBooleanArrayElements",
    "GetByteArrayElements",
    "GetCharArrayElements",
    "GetShortArrayElements",
    "GetIntArrayElements",
    "GetLongArrayElements",
    "GetFloatArrayElements",
    "GetDoubleArrayElements",
    "ReleaseBooleanArrayElements",
    "ReleaseByteArrayElements",
    "ReleaseCharArrayElements",
    "ReleaseShortArrayElements",
    "ReleaseIntArrayElements",
    "ReleaseLongArrayElements",
    "ReleaseFloatArrayElements",
    "ReleaseDoubleArrayElements",
    "GetBooleanArrayRegion",
    "GetByteArrayRegion",
    "GetCharArrayRegion",
    "GetShortArrayRegion",
    "GetIntArrayRegion",
    "GetLongArrayRegion",
    "GetFloatArrayRegion",
    "GetDoubleArrayRegion",
    "SetBooleanArrayRegion",
    "SetByteArrayRegion",
    "SetCharArrayRegion",
    "SetShortArrayRegion",
    "SetIntArrayRegion",
    "SetLongArrayRegion",
    "SetFloatArrayRegion",
    "SetDoubleArrayRegion",
    "RegisterNatives",
    "UnregisterNatives",
    "MonitorEnter",
    "MonitorExit",
    "GetJavaVM",
    "GetStringRegion",
    "GetStringUTFRegion",
    "GetPrimitiveArrayCritical",
    "ReleasePrimitiveArrayCritical",
    "GetStringCritical",
    "ReleaseStringCritical",
    "NewWeakGlobalRef",
    "DeleteWeakGlobalRef",
    "ExceptionCheck",
    "NewDirectByteBuffer",
    "GetDirectBufferAddress",
    "GetDirectBufferCapacity",
    "GetObjectRefType",
};

static void *trap_0(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[0], 0); return 0; }
static void *trap_1(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[1], 1); return 0; }
static void *trap_2(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[2], 2); return 0; }
static void *trap_3(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[3], 3); return 0; }
static void *trap_4(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[4], 4); return 0; }
static void *trap_5(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[5], 5); return 0; }
static void *trap_6(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[6], 6); return 0; }
static void *trap_7(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[7], 7); return 0; }
static void *trap_8(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[8], 8); return 0; }
static void *trap_9(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[9], 9); return 0; }
static void *trap_10(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[10], 10); return 0; }
static void *trap_11(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[11], 11); return 0; }
static void *trap_12(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[12], 12); return 0; }
static void *trap_13(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[13], 13); return 0; }
static void *trap_14(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[14], 14); return 0; }
static void *trap_15(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[15], 15); return 0; }
static void *trap_16(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[16], 16); return 0; }
static void *trap_17(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[17], 17); return 0; }
static void *trap_18(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[18], 18); return 0; }
static void *trap_19(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[19], 19); return 0; }
static void *trap_20(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[20], 20); return 0; }
static void *trap_21(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[21], 21); return 0; }
static void *trap_22(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[22], 22); return 0; }
static void *trap_23(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[23], 23); return 0; }
static void *trap_24(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[24], 24); return 0; }
static void *trap_25(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[25], 25); return 0; }
static void *trap_26(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[26], 26); return 0; }
static void *trap_27(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[27], 27); return 0; }
static void *trap_28(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[28], 28); return 0; }
static void *trap_29(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[29], 29); return 0; }
static void *trap_30(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[30], 30); return 0; }
static void *trap_31(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[31], 31); return 0; }
static void *trap_32(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[32], 32); return 0; }
static void *trap_33(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[33], 33); return 0; }
static void *trap_34(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[34], 34); return 0; }
static void *trap_35(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[35], 35); return 0; }
static void *trap_36(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[36], 36); return 0; }
static void *trap_37(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[37], 37); return 0; }
static void *trap_38(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[38], 38); return 0; }
static void *trap_39(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[39], 39); return 0; }
static void *trap_40(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[40], 40); return 0; }
static void *trap_41(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[41], 41); return 0; }
static void *trap_42(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[42], 42); return 0; }
static void *trap_43(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[43], 43); return 0; }
static void *trap_44(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[44], 44); return 0; }
static void *trap_45(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[45], 45); return 0; }
static void *trap_46(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[46], 46); return 0; }
static void *trap_47(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[47], 47); return 0; }
static void *trap_48(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[48], 48); return 0; }
static void *trap_49(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[49], 49); return 0; }
static void *trap_50(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[50], 50); return 0; }
static void *trap_51(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[51], 51); return 0; }
static void *trap_52(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[52], 52); return 0; }
static void *trap_53(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[53], 53); return 0; }
static void *trap_54(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[54], 54); return 0; }
static void *trap_55(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[55], 55); return 0; }
static void *trap_56(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[56], 56); return 0; }
static void *trap_57(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[57], 57); return 0; }
static void *trap_58(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[58], 58); return 0; }
static void *trap_59(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[59], 59); return 0; }
static void *trap_60(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[60], 60); return 0; }
static void *trap_61(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[61], 61); return 0; }
static void *trap_62(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[62], 62); return 0; }
static void *trap_63(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[63], 63); return 0; }
static void *trap_64(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[64], 64); return 0; }
static void *trap_65(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[65], 65); return 0; }
static void *trap_66(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[66], 66); return 0; }
static void *trap_67(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[67], 67); return 0; }
static void *trap_68(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[68], 68); return 0; }
static void *trap_69(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[69], 69); return 0; }
static void *trap_70(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[70], 70); return 0; }
static void *trap_71(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[71], 71); return 0; }
static void *trap_72(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[72], 72); return 0; }
static void *trap_73(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[73], 73); return 0; }
static void *trap_74(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[74], 74); return 0; }
static void *trap_75(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[75], 75); return 0; }
static void *trap_76(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[76], 76); return 0; }
static void *trap_77(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[77], 77); return 0; }
static void *trap_78(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[78], 78); return 0; }
static void *trap_79(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[79], 79); return 0; }
static void *trap_80(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[80], 80); return 0; }
static void *trap_81(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[81], 81); return 0; }
static void *trap_82(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[82], 82); return 0; }
static void *trap_83(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[83], 83); return 0; }
static void *trap_84(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[84], 84); return 0; }
static void *trap_85(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[85], 85); return 0; }
static void *trap_86(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[86], 86); return 0; }
static void *trap_87(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[87], 87); return 0; }
static void *trap_88(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[88], 88); return 0; }
static void *trap_89(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[89], 89); return 0; }
static void *trap_90(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[90], 90); return 0; }
static void *trap_91(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[91], 91); return 0; }
static void *trap_92(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[92], 92); return 0; }
static void *trap_93(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[93], 93); return 0; }
static void *trap_94(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[94], 94); return 0; }
static void *trap_95(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[95], 95); return 0; }
static void *trap_96(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[96], 96); return 0; }
static void *trap_97(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[97], 97); return 0; }
static void *trap_98(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[98], 98); return 0; }
static void *trap_99(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[99], 99); return 0; }
static void *trap_100(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[100], 100); return 0; }
static void *trap_101(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[101], 101); return 0; }
static void *trap_102(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[102], 102); return 0; }
static void *trap_103(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[103], 103); return 0; }
static void *trap_104(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[104], 104); return 0; }
static void *trap_105(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[105], 105); return 0; }
static void *trap_106(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[106], 106); return 0; }
static void *trap_107(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[107], 107); return 0; }
static void *trap_108(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[108], 108); return 0; }
static void *trap_109(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[109], 109); return 0; }
static void *trap_110(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[110], 110); return 0; }
static void *trap_111(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[111], 111); return 0; }
static void *trap_112(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[112], 112); return 0; }
static void *trap_113(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[113], 113); return 0; }
static void *trap_114(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[114], 114); return 0; }
static void *trap_115(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[115], 115); return 0; }
static void *trap_116(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[116], 116); return 0; }
static void *trap_117(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[117], 117); return 0; }
static void *trap_118(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[118], 118); return 0; }
static void *trap_119(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[119], 119); return 0; }
static void *trap_120(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[120], 120); return 0; }
static void *trap_121(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[121], 121); return 0; }
static void *trap_122(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[122], 122); return 0; }
static void *trap_123(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[123], 123); return 0; }
static void *trap_124(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[124], 124); return 0; }
static void *trap_125(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[125], 125); return 0; }
static void *trap_126(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[126], 126); return 0; }
static void *trap_127(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[127], 127); return 0; }
static void *trap_128(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[128], 128); return 0; }
static void *trap_129(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[129], 129); return 0; }
static void *trap_130(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[130], 130); return 0; }
static void *trap_131(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[131], 131); return 0; }
static void *trap_132(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[132], 132); return 0; }
static void *trap_133(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[133], 133); return 0; }
static void *trap_134(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[134], 134); return 0; }
static void *trap_135(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[135], 135); return 0; }
static void *trap_136(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[136], 136); return 0; }
static void *trap_137(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[137], 137); return 0; }
static void *trap_138(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[138], 138); return 0; }
static void *trap_139(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[139], 139); return 0; }
static void *trap_140(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[140], 140); return 0; }
static void *trap_141(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[141], 141); return 0; }
static void *trap_142(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[142], 142); return 0; }
static void *trap_143(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[143], 143); return 0; }
static void *trap_144(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[144], 144); return 0; }
static void *trap_145(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[145], 145); return 0; }
static void *trap_146(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[146], 146); return 0; }
static void *trap_147(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[147], 147); return 0; }
static void *trap_148(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[148], 148); return 0; }
static void *trap_149(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[149], 149); return 0; }
static void *trap_150(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[150], 150); return 0; }
static void *trap_151(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[151], 151); return 0; }
static void *trap_152(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[152], 152); return 0; }
static void *trap_153(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[153], 153); return 0; }
static void *trap_154(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[154], 154); return 0; }
static void *trap_155(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[155], 155); return 0; }
static void *trap_156(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[156], 156); return 0; }
static void *trap_157(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[157], 157); return 0; }
static void *trap_158(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[158], 158); return 0; }
static void *trap_159(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[159], 159); return 0; }
static void *trap_160(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[160], 160); return 0; }
static void *trap_161(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[161], 161); return 0; }
static void *trap_162(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[162], 162); return 0; }
static void *trap_163(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[163], 163); return 0; }
static void *trap_164(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[164], 164); return 0; }
static void *trap_165(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[165], 165); return 0; }
static void *trap_166(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[166], 166); return 0; }
static void *trap_167(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[167], 167); return 0; }
static void *trap_168(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[168], 168); return 0; }
static void *trap_169(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[169], 169); return 0; }
static void *trap_170(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[170], 170); return 0; }
static void *trap_171(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[171], 171); return 0; }
static void *trap_172(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[172], 172); return 0; }
static void *trap_173(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[173], 173); return 0; }
static void *trap_174(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[174], 174); return 0; }
static void *trap_175(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[175], 175); return 0; }
static void *trap_176(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[176], 176); return 0; }
static void *trap_177(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[177], 177); return 0; }
static void *trap_178(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[178], 178); return 0; }
static void *trap_179(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[179], 179); return 0; }
static void *trap_180(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[180], 180); return 0; }
static void *trap_181(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[181], 181); return 0; }
static void *trap_182(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[182], 182); return 0; }
static void *trap_183(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[183], 183); return 0; }
static void *trap_184(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[184], 184); return 0; }
static void *trap_185(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[185], 185); return 0; }
static void *trap_186(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[186], 186); return 0; }
static void *trap_187(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[187], 187); return 0; }
static void *trap_188(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[188], 188); return 0; }
static void *trap_189(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[189], 189); return 0; }
static void *trap_190(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[190], 190); return 0; }
static void *trap_191(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[191], 191); return 0; }
static void *trap_192(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[192], 192); return 0; }
static void *trap_193(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[193], 193); return 0; }
static void *trap_194(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[194], 194); return 0; }
static void *trap_195(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[195], 195); return 0; }
static void *trap_196(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[196], 196); return 0; }
static void *trap_197(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[197], 197); return 0; }
static void *trap_198(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[198], 198); return 0; }
static void *trap_199(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[199], 199); return 0; }
static void *trap_200(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[200], 200); return 0; }
static void *trap_201(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[201], 201); return 0; }
static void *trap_202(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[202], 202); return 0; }
static void *trap_203(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[203], 203); return 0; }
static void *trap_204(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[204], 204); return 0; }
static void *trap_205(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[205], 205); return 0; }
static void *trap_206(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[206], 206); return 0; }
static void *trap_207(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[207], 207); return 0; }
static void *trap_208(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[208], 208); return 0; }
static void *trap_209(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[209], 209); return 0; }
static void *trap_210(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[210], 210); return 0; }
static void *trap_211(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[211], 211); return 0; }
static void *trap_212(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[212], 212); return 0; }
static void *trap_213(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[213], 213); return 0; }
static void *trap_214(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[214], 214); return 0; }
static void *trap_215(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[215], 215); return 0; }
static void *trap_216(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[216], 216); return 0; }
static void *trap_217(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[217], 217); return 0; }
static void *trap_218(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[218], 218); return 0; }
static void *trap_219(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[219], 219); return 0; }
static void *trap_220(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[220], 220); return 0; }
static void *trap_221(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[221], 221); return 0; }
static void *trap_222(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[222], 222); return 0; }
static void *trap_223(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[223], 223); return 0; }
static void *trap_224(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[224], 224); return 0; }
static void *trap_225(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[225], 225); return 0; }
static void *trap_226(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[226], 226); return 0; }
static void *trap_227(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[227], 227); return 0; }
static void *trap_228(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[228], 228); return 0; }
static void *trap_229(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[229], 229); return 0; }
static void *trap_230(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[230], 230); return 0; }
static void *trap_231(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[231], 231); return 0; }
static void *trap_232(void *a, void *b, void *c, void *d) { (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) \xe8\xa2\xab\xe8\xb0\x83\xe7\x94\xa8\\n", g_trap_names[232], 232); return 0; }

static void *JNI_TRAPS[JNI_SLOT_COUNT] = {
    trap_0,
    trap_1,
    trap_2,
    trap_3,
    trap_4,
    trap_5,
    trap_6,
    trap_7,
    trap_8,
    trap_9,
    trap_10,
    trap_11,
    trap_12,
    trap_13,
    trap_14,
    trap_15,
    trap_16,
    trap_17,
    trap_18,
    trap_19,
    trap_20,
    trap_21,
    trap_22,
    trap_23,
    trap_24,
    trap_25,
    trap_26,
    trap_27,
    trap_28,
    trap_29,
    trap_30,
    trap_31,
    trap_32,
    trap_33,
    trap_34,
    trap_35,
    trap_36,
    trap_37,
    trap_38,
    trap_39,
    trap_40,
    trap_41,
    trap_42,
    trap_43,
    trap_44,
    trap_45,
    trap_46,
    trap_47,
    trap_48,
    trap_49,
    trap_50,
    trap_51,
    trap_52,
    trap_53,
    trap_54,
    trap_55,
    trap_56,
    trap_57,
    trap_58,
    trap_59,
    trap_60,
    trap_61,
    trap_62,
    trap_63,
    trap_64,
    trap_65,
    trap_66,
    trap_67,
    trap_68,
    trap_69,
    trap_70,
    trap_71,
    trap_72,
    trap_73,
    trap_74,
    trap_75,
    trap_76,
    trap_77,
    trap_78,
    trap_79,
    trap_80,
    trap_81,
    trap_82,
    trap_83,
    trap_84,
    trap_85,
    trap_86,
    trap_87,
    trap_88,
    trap_89,
    trap_90,
    trap_91,
    trap_92,
    trap_93,
    trap_94,
    trap_95,
    trap_96,
    trap_97,
    trap_98,
    trap_99,
    trap_100,
    trap_101,
    trap_102,
    trap_103,
    trap_104,
    trap_105,
    trap_106,
    trap_107,
    trap_108,
    trap_109,
    trap_110,
    trap_111,
    trap_112,
    trap_113,
    trap_114,
    trap_115,
    trap_116,
    trap_117,
    trap_118,
    trap_119,
    trap_120,
    trap_121,
    trap_122,
    trap_123,
    trap_124,
    trap_125,
    trap_126,
    trap_127,
    trap_128,
    trap_129,
    trap_130,
    trap_131,
    trap_132,
    trap_133,
    trap_134,
    trap_135,
    trap_136,
    trap_137,
    trap_138,
    trap_139,
    trap_140,
    trap_141,
    trap_142,
    trap_143,
    trap_144,
    trap_145,
    trap_146,
    trap_147,
    trap_148,
    trap_149,
    trap_150,
    trap_151,
    trap_152,
    trap_153,
    trap_154,
    trap_155,
    trap_156,
    trap_157,
    trap_158,
    trap_159,
    trap_160,
    trap_161,
    trap_162,
    trap_163,
    trap_164,
    trap_165,
    trap_166,
    trap_167,
    trap_168,
    trap_169,
    trap_170,
    trap_171,
    trap_172,
    trap_173,
    trap_174,
    trap_175,
    trap_176,
    trap_177,
    trap_178,
    trap_179,
    trap_180,
    trap_181,
    trap_182,
    trap_183,
    trap_184,
    trap_185,
    trap_186,
    trap_187,
    trap_188,
    trap_189,
    trap_190,
    trap_191,
    trap_192,
    trap_193,
    trap_194,
    trap_195,
    trap_196,
    trap_197,
    trap_198,
    trap_199,
    trap_200,
    trap_201,
    trap_202,
    trap_203,
    trap_204,
    trap_205,
    trap_206,
    trap_207,
    trap_208,
    trap_209,
    trap_210,
    trap_211,
    trap_212,
    trap_213,
    trap_214,
    trap_215,
    trap_216,
    trap_217,
    trap_218,
    trap_219,
    trap_220,
    trap_221,
    trap_222,
    trap_223,
    trap_224,
    trap_225,
    trap_226,
    trap_227,
    trap_228,
    trap_229,
    trap_230,
    trap_231,
    trap_232,
};
