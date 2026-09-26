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
rem 运行时不再依赖 unidbg（2026-09-25 定案：ARM 原生码一律在 QEMU guest 里执行），所以编译类路径里
rem 没有 vendor\unidbg\* —— 这是有意为之：哪天有人把 unidbg 的调用加回 src\，这里会直接编译不过。
rem （曾经需要它，是因为 GuardSession 用 com.github.unidbg.* 跑 ARM so。）
set CP=vendor\deps\org-json.jar;vendor\deps\gson.jar;vendor\deps\okhttp3.jar;vendor\deps\okio.jar;vendor\deps\asm-9.5.jar
rem --release 17 so bridge.jar also runs on the Alpine OpenJDK **17** inside the QEMU guest
rem (2026-09-25: guest bridge.Server answered the line protocol fine, but a default-21 jar died
rem with UnsupportedClassVersionError 65.0 > 61.0). The bundled host JRE is 21, so this costs
rem nothing there; unidbg's unpacker.jar is still major 65, so FindJavaExe keeps picking the
rem highest available java. Comments here must stay ASCII: this file is read as GBK by cmd.exe.
"%JAVAC%" -encoding UTF-8 --release 17 -cp "%CP%" -d build @sources.txt || exit /b 3
rem Package from JavaBridge itself. The old "cd build" + "-C build ." looked for build\build\ and
rem both jar invocations failed silently, so bridge.jar was never actually updated.
(if exist bridge.jar del bridge.jar)
set JARTOOL=jar
if defined JAVA_HOME if exist "%JAVA_HOME%\bin\jar.exe" set JARTOOL="%JAVA_HOME%\bin\jar.exe"
%JARTOOL% --create --file bridge.jar -C build . || exit /b 4
if not exist bridge.jar (
  echo bridge.jar not created: check jar.exe on PATH or under JAVA_HOME >&2
  exit /b 4
)
echo bridge.jar built.
