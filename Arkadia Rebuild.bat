dotnet publish Arkadia.csproj /p:PublishProfile=win-x64-portable
if errorlevel 1 (
    echo Publish failed — xcopy skipped.
    exit /b 1
)
xcopy /Y /E publish\win-x64\* G:\Arkadia\