#!/bin/bash
# §6.10 Step4k：cdex →（baksmali）→ smali →（smali 汇编）→ 标准 dex，45 件全量往返
cd /root/x86guest
CP='smali-jar/baksmali-2.5.2.jar:smali-jar/dexlib2-2.5.2.jar:smali-jar/util-2.5.2.jar:smali-jar/guava-27.1-android.jar:smali-jar/jcommander-1.64.jar:smali-jar/jsr305-3.0.2.jar'
CP2='smali-jar/smali-2.5.2.jar:smali-jar/dexlib2-2.5.2.jar:smali-jar/util-2.5.2.jar:smali-jar/guava-27.1-android.jar:smali-jar/jcommander-1.64.jar:smali-jar/jsr305-3.0.2.jar'

# ① 先单件 smoke（disassemble 子命令名验证）
if [ ! -d /tmp/smoke ] || [ "$(find /tmp/smoke -name '*.smali' | wc -l)" -eq 0 ]; then
  rm -rf /tmp/smoke && mkdir -p /tmp/smoke
  java -cp "$CP" org.jf.baksmali.Main disassemble dex-out/boot-android.test.base_classes.cdex -o /tmp/smoke 2>&1 | head -5
fi
n=$(find /tmp/smoke -name '*.smali' | wc -l)
echo "smoke: $n 个 smali"
[ "$n" -eq 0 ] && { echo '!! baksmali 对 cdex 不工作'; exit 1; }

# ② 往返验证：smoke 件汇编回标准 dex
rm -f /tmp/smoke_std.dex
java -cp "$CP2" org.jf.smali.Main assemble /tmp/smoke -o /tmp/smoke_std.dex 2>&1 | head -4
ls -la /tmp/smoke_std.dex && od -A x -t x1z -v /tmp/smoke_std.dex | head -1

# ③ 全量往返（45 件；smali 文本 ~2-4GB，磁盘扛得住；用 nohup 后台跑）
if [ -f /tmp/smoke_std.dex ] && [ $(stat -c%s /tmp/smoke_std.dex) -gt 10000 ]; then
  nohup bash -c '
    cd /root/x86guest
    CP="smali-jar/baksmali-2.5.2.jar:smali-jar/dexlib2-2.5.2.jar:smali-jar/util-2.5.2.jar:smali-jar/guava-27.1-android.jar:smali-jar/jcommander-1.64.jar:smali-jar/jsr305-3.0.2.jar"
    CP2="smali-jar/smali-2.5.2.jar:smali-jar/dexlib2-2.5.2.jar:smali-jar/util-2.5.2.jar:smali-jar/guava-27.1-android.jar:smali-jar/jcommander-1.64.jar:smali-jar/jsr305-3.0.2.jar"
    mkdir -p dex-std smali-tmp
    for f in dex-out/*.cdex; do
      b=$(basename "$f" .cdex)
      rm -rf smali-tmp/$b; mkdir -p smali-tmp/$b
      java -Xmx1g -cp "$CP" org.jf.baksmali.Main disassemble "$f" -o smali-tmp/$b >> roundtrip.log 2>&1 || { echo "BAKSMALI_FAIL $b" >> roundtrip.log; continue; }
      java -Xmx1g -cp "$CP2" org.jf.smali.Main assemble smali-tmp/$b -o dex-std/$b.dex >> roundtrip.log 2>&1 || { echo "SMALI_FAIL $b" >> roundtrip.log; continue; }
      echo "OK $b $(stat -c%s dex-std/$b.dex)" >> roundtrip.log
      rm -rf smali-tmp/$b
    done
    echo ALL_DONE >> roundtrip.log
  ' > /dev/null 2>&1 &
  echo "往返已在后台开始（roundtrip.log 跟踪）"
fi
