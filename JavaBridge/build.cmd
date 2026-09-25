@echo off
rem Build JavaBridge: android-stub + crawler stub (Spider/SpiderApi/SpiderDebug) + bridge server -> bridge.jar
rem Compile classpath needs org-json (server protocol) plus the same runtime deps the spider
rem jars link against: gson (SpiderApi.multiReq) and okhttp3/okio (Spider.client/safeDns).
setlocal
set JAVA=java
if defined JAVA_HOME set JAVA=%JAVA_HOME%\bin\java.exe
set JAVAC=
if defined JAVA_HOME if exist "%JAVA_HOME%\bin\javac.exe" set JAVAC=%JAVA_HOME%\bin\javac.exe
if not defined JAVAC for /d %%D in ("C:\Program Files\Java\*") do if exist "%%D\bin\javac.exe" set JAVAC=%%D\bin\javac.exe
if not defined JAVAC for /d %%D in ("C:\Program Files\Android\openjdk\*") do if exist "%%D\bin\javac.exe" set JAVAC=%%D\bin\javac.exe
if not defined JAVAC where javac >nul 2>nul && set JAVAC=javac
if not defined JAVAC (
  echo javac.exe not found. Install JDK 17+. >&2
  exit /b 2
)
if not exist build mkdir build
dir /s /b src\*.java > sources.txt
rem unidbg must be on the compile classpath: GuardSession uses com.github.unidbg.* to unpack
rem the ARM .so. With only deps\ on CP you get 40 "cannot find symbol DvmObject" errors (2026-09-24).
set CP=vendor\deps\org-json.jar;vendor\deps\gson.jar;vendor\deps\okhttp3.jar;vendor\deps\okio.jar;vendor\deps\asm-9.5.jar;vendor\unidbg\*
"%JAVAC%" -encoding UTF-8 -cp "%CP%" -d build @sources.txt || exit /b 3
rem Package from JavaBridge itself. The old "cd build" + "-C build ." looked for build\build\ and
rem both jar invocations failed silently, so bridge.jar was never actually updated.
(if exist bridge.jar del bridge.jar)
"%JAVA_HOME%\bin\jar.exe" --create --file bridge.jar -C build . 2>nul || jar --create --file bridge.jar -C build . || exit /b 4
if not exist bridge.jar (
  echo bridge.jar not created: check jar.exe on PATH or under JAVA_HOME >&2
  exit /b 4
)
echo bridge.jar built.
