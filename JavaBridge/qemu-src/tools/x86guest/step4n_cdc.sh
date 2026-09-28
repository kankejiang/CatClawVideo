#!/bin/bash
# §6.10 Step4n：下载 fOmey/compact_dex_converter 并侦察
cd /root/x86guest
rm -f cdc.zip; rm -rf compact_dex_converter-master compact_dex_converter
curl -sL --max-time 60 'https://codeload.github.com/fOmey/compact_dex_converter/zip/refs/heads/master' -o cdc.zip
ls -la cdc.zip
unzip -q cdc.zip && ls compact_dex_converter-master/
find compact_dex_converter-master -maxdepth 2 | head -30
