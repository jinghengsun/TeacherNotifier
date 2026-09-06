$ErrorActionPreference = 'SilentlyContinue'
$src = 'C:\Users\jingh\Downloads\TeacherNotifier\bin\Release\net8.0-windows'
$dst = 'C:\Users\jingh\Downloads\ClassIsland_app_windows_x64_selfContained_folder\data\Plugins\com.jingh.teacher-notifier'
$ci  = 'C:\Users\jingh\Downloads\ClassIsland_app_windows_x64_selfContained_folder'

Stop-Process -Name 'ClassIsland.Desktop' -Force
Start-Sleep -Seconds 2
Copy-Item "$src\*" "$dst\" -Force
Start-Sleep -Seconds 1
Start-Process -FilePath "$ci\ClassIsland.exe" -WorkingDirectory $ci
Write-Output 'done'
