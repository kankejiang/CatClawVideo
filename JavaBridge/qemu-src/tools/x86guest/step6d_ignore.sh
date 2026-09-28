#!/bin/bash
# §6.10 Step6d：读 parsed_options.cc 的 Ignore 列表与 DoParse
cd /root/x86guest/artsrc
echo '=== 330-360（.Ignore 列表）==='
sed -n '330,360p' parsed_options.cc
echo
echo '=== 470-520（DoParse 里对 ignore_unrecognized/来源的处理）==='
sed -n '470,520p' parsed_options.cc
