@echo off
rem Build the Guard unpacker (unidbg-based) -> vendor\unidbg\unpacker.jar
rem
rem unidbg deps live in vendor\unidbg\*.jar (vendored, same policy as vendor\dex2jar).
rem Note: the POM of unidbg-android-0.9.9 omits several jars that are needed at runtime
rem (jna / jna-platform / slf4j-api / slf4j-simple / native-lib-loader) - they are vendored too.
setlocal
set JAVA=java
if defined JAVA_HOME set JAVA=%JAVA_HOME%\bin\java.exe
set JAVAC=
if defined JAVA_HOME if exist "%JAVA_HOME%\bin\javac.exe" set JAVAC=%JAVA_HOME%\bin\javac.exe
if not defined JAVAC for /d %%D in ("C:\Program Files\Java\*") do if exist "%%D\bin\javac.exe" set JAVAC=%%D\bin\javac.exe
if not defined JAVAC for /d %%D in ("C:\Program Files\Microsoft\jdk-*") do if exist "%%D\bin\javac.exe" set JAVAC=%%D\bin\javac.exe
if not defined JAVAC where javac >nul 2>nul && set JAVAC=javac
if not defined JAVAC (
  echo javac.exe not found. Install JDK 17+. >&2
  exit /b 2
)

if not exist vendor\unidbg\unidbg-android-0.9.9.jar (
  echo vendor\unidbg is missing unidbg jars. >&2
  exit /b 3
)

if not exist build-unidbg mkdir build-unidbg
"%JAVAC%" -encoding UTF-8 -cp "vendor\unidbg\*" -d build-unidbg ^
    unidbg-src\bridge\GuardUnpacker.java ^
    unidbg-src\bridge\GuardJni.java || exit /b 4

"%JAVA%" -cp "build-unidbg" -version >nul 2>nul
cd build-unidbg
jar --create --file ..\vendor\unidbg\unpacker.jar -C . . || exit /b 5
cd ..
echo vendor\unidbg\unpacker.jar built.
