#!/bin/bash
# §6.10 Step6Q：干净配置（只留 -Xnorelocate）+ A/B 对照
cd /root/x86guest

run_round() {
  local tag=$1 extra=$2
  python3 - "$extra" <<'PYEOF'
import sys, re
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
s = re.sub(r'export CATCLAW_JVM_EXTRA="[^"]*"', 'export CATCLAW_JVM_EXTRA="%s"' % sys.argv[1], s)
open(p, 'w', encoding='utf-8').write(s)
PYEOF
  echo "════ $tag（extra=\"$extra\"）════"
  : > /tmp/fakelogd.log
  bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪'
  echo -n "  image 回退计数: "; grep -ac 'Could not create image space' boottest.log
  echo -n "  image 成功线索: "; grep -ac 'ImageSpace\|image loaded\|HasBootImageSpace' boottest.log
}

run_round 'A：-Xnorelocate（预期 image 加载）' '-Xnorelocate'
run_round 'B：对照组（不传，预期回退 imageless）' ''
