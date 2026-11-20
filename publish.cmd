@echo off
rem Сборка публикации SmartRelay для Windows: publish\win-x64
rem Запускать двойным щелчком из папки проекта (где лежит SmartRelay.csproj).
setlocal
cd /d "%~dp0"

echo === Публикация SmartRelay (win-x64) ===
dotnet publish -c Release -r win-x64 --self-contained false -o publish\win-x64
if errorlevel 1 goto fail

echo.
echo === Проверка собранного файла ===
publish\win-x64\SmartRelay.exe --version
echo.
echo Готово: папка publish\win-x64
echo Скопируйте её целиком и положите рядом свой smartrelay.json
goto end

:fail
echo.
echo ОШИБКА сборки. Проверьте, что установлен .NET 7 SDK (dotnet --info).

:end
endlocal
pause
