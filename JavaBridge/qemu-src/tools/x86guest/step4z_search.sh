#!/bin/bash
# §6.10 Step4z：GitHub 搜索 Linux x86_64 原生 dex2oat（host 工具链，可编 arm64 目标）
echo '=== 仓库搜索 1: dex2oat linux ==='
curl -s --max-time 25 'https://api.github.com/search/repositories?q=dex2oat+in:name,description&per_page=10' | grep '"full_name"' | head -10
echo '=== 仓库搜索 2: vdex deodex toolchain ==='
curl -s --max-time 25 'https://api.github.com/search/repositories?q=vdex+extractor+binary&per_page=6' | grep '"full_name"' | head -6
