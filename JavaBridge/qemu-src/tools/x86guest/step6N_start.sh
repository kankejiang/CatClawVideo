#!/bin/bash
# §6.10 Step6N：读无参 Runtime::Start() 的开头——runtime_options 的来源
cd /root/x86guest/artsrc
echo '=== Start() 定义行 ==='
grep -n 'bool Runtime::Start()' runtime.cc
echo '=== Start() 开头 60 行 ==='
start=$(grep -n 'bool Runtime::Start()' runtime.cc | head -1 | cut -d: -f1)
sed -n "${start},$((start+60))p" runtime.cc
