# ⚠ 这三行必须与**我们镜像里的 boot*.art 集合**逐条对齐（15 条，1:1 无多余），
#    不能照抄 108 的运行时值（它 31 条，其中 19 个 boot-*.art 我们这张 initrd 根本没有
#    ⇒ ART 打不开 boot image 直接 abort —— 实测 zygote 退出码 134 且 stderr 零字，
#    与 N1-2/N1-3 的 diff 一起定位，见 docs/research/framework/路线对比与推荐.md）。
export BOOTCLASSPATH=/apex/com.android.art/javalib/core-oj.jar:/apex/com.android.art/javalib/core-libart.jar:/apex/com.android.i18n/javalib/core-icu4j.jar:/apex/com.android.art/javalib/okhttp.jar:/apex/com.android.art/javalib/bouncycastle.jar:/apex/com.android.art/javalib/apache-xml.jar:/apex/com.android.conscrypt/javalib/conscrypt.jar:/system/framework/framework.jar:/system/framework/ext.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/android.test.base.jar
export DEX2OATBOOTCLASSPATH=/apex/com.android.art/javalib/core-oj.jar:/apex/com.android.art/javalib/core-libart.jar:/apex/com.android.i18n/javalib/core-icu4j.jar:/apex/com.android.art/javalib/okhttp.jar:/apex/com.android.art/javalib/bouncycastle.jar:/apex/com.android.art/javalib/apache-xml.jar:/apex/com.android.conscrypt/javalib/conscrypt.jar:/system/framework/framework.jar:/system/framework/ext.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/android.test.base.jar
export SYSTEMSERVERCLASSPATH=/system/framework/services.jar:/system/framework/com.android.location.provider.jar:/apex/com.android.art/javalib/service-art.jar
# === E2/N1-2：108 zygote64 的 /proc/PID/environ 里我们缺的变量（逐字取回）===
export ANDROID_ASSETS=/system/app
export ANDROID_BOOTLOGO=1
export ASEC_MOUNTPOINT=/mnt/asec
export PATH=/product/bin:/apex/com.android.runtime/bin:/apex/com.android.art/bin:/system_ext/bin:/system/bin:/system/xbin:/odm/bin:/vendor/bin:/vendor/xbin
export STANDALONE_SYSTEMSERVER_JARS=/apex/com.android.btservices/javalib/service-bluetooth.jar:/apex/com.android.os.statsd/javalib/service-statsd.jar:/apex/com.android.scheduling/javalib/service-scheduling.jar:/apex/com.android.tethering/javalib/service-connectivity.jar:/apex/com.android.uwb/javalib/service-uwb.jar:/apex/com.android.wifi/javalib/service-wifi.jar
export TERMINFO=/system_ext/etc/terminfo
