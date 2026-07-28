@ECHO OFF
SETLOCAL
SET DIR=%~dp0

SET JAVA_EXE=java.exe
IF DEFINED JAVA_HOME (
  SET JAVA_EXE=%JAVA_HOME%\bin\java.exe
)

REM Prefer Android Studio JBR if present
IF EXIST "%LOCALAPPDATA%\Programs\Android Studio\jbr\bin\java.exe" (
  SET JAVA_EXE=%LOCALAPPDATA%\Programs\Android Studio\jbr\bin\java.exe
)
IF EXIST "%ProgramFiles%\Android\Android Studio\jbr\bin\java.exe" (
  SET JAVA_EXE=%ProgramFiles%\Android\Android Studio\jbr\bin\java.exe
)

SET CLASSPATH=%DIR%gradle\wrapper\gradle-wrapper.jar
"%JAVA_EXE%" %DEFAULT_JVM_OPTS% %JAVA_OPTS% %GRADLE_OPTS% "-Dorg.gradle.appname=gradlew" -classpath "%CLASSPATH%" org.gradle.wrapper.GradleWrapperMain %*
