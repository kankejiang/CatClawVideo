#!/bin/bash
# §6.10 Step4j：baksmali 读 cdex smoke test（规范化行尾后）
cd /root/x86guest
sed -i 's/\r$//' step4j_smoke.sh
CP='smali-jar/baksmali-2.5.2.jar:smali-jar/dexlib2-2.5.2.jar:smali-jar/util-2.5.2.jar:smali-jar/guava-27.1-android.jar:smali-jar/jcommander-1.64.jar:smali-jar/jsr305-3.0.2.jar'
echo "CP=$CP"
rm -rf /tmp/smoke && mkdir -p /tmp/smoke
java -cp "$CP" org.jf.baksmali.Main d dex-out/boot-android.test.base_classes.cdex -o /tmp/smoke 2>&1 | tail -4
echo "smali 文件数: $(find /tmp/smoke -name '*.smali' | wc -l)"
F=$(find /tmp/smoke -name '*.smali' | head -1)
[ -n "$F" ] && head -8 "$F"
