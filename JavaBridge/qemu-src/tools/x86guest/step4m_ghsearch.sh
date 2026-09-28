#!/bin/bash
# §6.10 Step4m：GitHub API 搜索 compact_dex_converter 的 rehost 仓库
echo '=== 仓库搜索 ==='
curl -s --max-time 25 'https://api.github.com/search/repositories?q=compact_dex_converter&per_page=10' | grep -E '"full_name"|"html_url".*compact' | head -12
echo '=== 备选关键词 cdex2dex / cdex converter ==='
curl -s --max-time 25 'https://api.github.com/search/repositories?q=cdex+converter+dex&per_page=8' | grep '"full_name"' | head -8
