#!/bin/bash
# §6.10 Step4i v2：maven thin jar + 依赖组合（guava 27.1-android / dexlib2 / util / jcommander）
set -x
cd /root/x86guest/smali-jar
MV=https://repo1.maven.org/maven2
curl -sL --max-time 40 -o dexlib2-2.5.2.jar $MV/org/smali/dexlib2/2.5.2/dexlib2-2.5.2.jar
curl -sL --max-time 40 -o util-2.5.2.jar $MV/org/smali/util/2.5.2/util-2.5.2.jar
curl -sL --max-time 40 -o guava-27.1-android.jar $MV/com/google/guava/guava/27.1-android/guava-27.1-android.jar
curl -sL --max-time 40 -o jcommander-1.64.jar $MV/com/beust/jcommander/1.64/jcommander-1.64.jar
curl -sL --max-time 40 -o jsr305-3.0.2.jar $MV/com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar
ls -la
CP=baksmali-2.5.2.jar:dexlib2-2.5.2.jar:util-2.5.2.jar:guava-27.1-android.jar:jcommander-1.64.jar:jsr305-3.0.2.jar

cd /root/x86guest
echo '=== smoke test: baksmali 读 cdex（最小件 android.test.base）==='
rm -rf /tmp/smoke && mkdir -p /tmp/smoke
java -cp smali-jar/$CP org.jf.baksmali.Main d dex-out/boot-android.test.base_classes.cdex -o /tmp/smoke 2>&1 | tail -4
echo "smali 文件数: $(find /tmp/smoke -name '*.smali' | wc -l)"
head -12 $(find /tmp/smoke -name '*.smali' | head -1)
