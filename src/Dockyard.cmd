@echo off
if exist "C:\Users\nhueb\AppData\Local\Dockyard\app\Dockyard.exe" ( start "" "C:\Users\nhueb\AppData\Local\Dockyard\app\Dockyard.exe" & exit /b 0 )
start "" /b "C:\Program Files\dotnet\dotnet.exe" "C:\Users\nhueb\AppData\Local\Dockyard\app\Dockyard.dll"
