@echo off
rem Build JavaBridge: android-stub + crawler stub + bridge server -> bridge.jar
setlocal
set JAVA=java
if defined JAVA_HOME set JAVA=%JAVA_HOME%\bin\java.exe
where javac >nul 2>nul && set JAVAC=javac
if not defined JAVAC (
  for /d %%D in ("C:\Program Files\Java\*") do if exist "%%D\bin\javac.exe" set JAVAC=%%D\bin\javac.exe
)
if not defined JAVAC (
  echo javac.exe not found. Install JDK 17+. >&2
  exit /b 2
)
if not exist build mkdir build
dir /s /b src\*.java > sources.txt
%JAVAC% -encoding UTF-8 -cp "vendor\deps\org-json.jar" -d build @sources.txt || exit /b 3
cd build
"%JAVA_HOME%\bin\jar.exe" --create --file ..\bridge.jar -C build . 2>nul || jar --create --file ..\bridge.jar -C build .
cd ..
echo bridge.jar built.
