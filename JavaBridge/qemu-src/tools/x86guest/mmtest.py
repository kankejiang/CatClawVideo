# -*- coding: utf-8 -*-
import mmap
try:
    m = mmap.mmap(-1, 1610612736)
    print('host mmap 1.5GB OK')
    m.close()
except Exception as e:
    print('host mmap FAIL:', e)
