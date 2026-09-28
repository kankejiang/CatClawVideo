#!/bin/bash
# §6.10 Step4y：抓 abort message（多种关键词）
grep -a 'Abort message' /tmp/fakelogd.log | tail -3
echo '---'
grep -ai 'check failed' /tmp/fakelogd.log | tail -3
echo '---'
grep -a 'art::' /tmp/fakelogd.log | grep -a -iE 'fatal|abort|check' | tail -4
echo '---'
grep -a 'Runtime aborting' /tmp/fakelogd.log | tail -2
