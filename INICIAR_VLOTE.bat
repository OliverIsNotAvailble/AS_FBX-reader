@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERRO] .NET SDK nao encontrado. Instale o SDK .NET 9.
    pause
    exit /b 1
)
echo.
echo Iniciando AS_FBX-reader - VLote (dotnet run -c Release)...
echo.
dotnet run --project "src\AS.FBXReader\AS.FBXReader.csproj" --configuration Release
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo.
    echo [ERRO] O Reader encerrou com codigo %RESULT%.
    pause
)
exit /b %RESULT%
