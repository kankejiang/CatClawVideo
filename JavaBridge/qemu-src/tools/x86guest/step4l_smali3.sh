#!/bin/bash
# §6.10 Step4l v2：确认 smali 3.x 的 group/artifact 布局
echo '=== maven central: com/android/tools/smali/ 组 ==='
curl -s --max-time 20 'https://repo1.maven.org/maven2/com/android/tools/smali/' | grep -oE 'title="[^"]*/"' | head -12
echo '=== google maven 索引尝试 ==='
curl -s --max-time 20 'https://dl.google.com/dl/android/maven2/com/android/tools/smali/baksmali/maven-metadata.xml' | head -8
