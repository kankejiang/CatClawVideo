#!/bin/bash
# §6.10 Step5a：批量拉候选 dex2oat 仓库找 Linux x86_64 原生二进制
cd /root/x86guest
mkdir -p dex2oat-candidates && cd dex2oat-candidates
for repo in pakhozako/dex2oat fansangg/dex2oat idyll5988/dex2oat github-maolin/dex2oat iamlooper/dex2oat-optimizer; do
  name=$(echo $repo | tr '/' '_')
  [ -d "$name" ] && continue
  echo "== $repo =="
  curl -sL --max-time 40 "https://codeload.github.com/$repo/zip/refs/heads/master" -o $name.zip
  sz=$(stat -c%s $name.zip 2>/dev/null || echo 0)
  if [ "$sz" -lt 1000 ]; then
    curl -sL --max-time 40 "https://codeload.github.com/$repo/zip/refs/heads/main" -o $name.zip
    sz=$(stat -c%s $name.zip 2>/dev/null || echo 0)
  fi
  echo "  zip=$sz"
  [ "$sz" -gt 1000 ] && unzip -q -o $name.zip && rm $name.zip
done
echo '=== 搜寻 ELF dex2oat ==='
find . -type f \( -name 'dex2oat*' -o -name '*.zip' \) | head -20
for f in $(find . -type f -name 'dex2oat*'); do
  echo "--- $f ---"; file "$f" | head -1
done
