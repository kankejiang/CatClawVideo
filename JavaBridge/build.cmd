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
set CP=vendor\deps\org-json.jar;vendor\deps\gson.jar;vendor\deps\okhttp3.jar;vendor\deps\okio.jar
"%JAVAC%" -encoding UTF-8 -cp "%CP%" -d build @sources.txt || exit /b 3
cd build
"%JAVA_HOME%\bin\jar.exe" --create --file ..\bridge.jar -C build . 2>nul || jar --create --file ..\bridge.jar -C build .
cd ..
echo bridge.jar built.
