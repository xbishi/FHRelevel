@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
echo ============================================
echo  FH Relevel 重新升级修改器 - 安装程序
echo ============================================
echo.

set "GAME="

rem 常见 Steam 安装路径自动探测
for %%D in (
  "C:\Program Files (x86)\Steam\steamapps\common\Frosthaven"
  "C:\Program Files\Steam\steamapps\common\Frosthaven"
) do if exist "%%~D\Frosthaven.exe" set "GAME=%%~D"

for %%L in (C D E F G H I J) do (
  if not defined GAME if exist "%%L:\SteamLibrary\steamapps\common\Frosthaven\Frosthaven.exe" set "GAME=%%L:\SteamLibrary\steamapps\common\Frosthaven"
  if not defined GAME if exist "%%L:\Steam\steamapps\common\Frosthaven\Frosthaven.exe" set "GAME=%%L:\Steam\steamapps\common\Frosthaven"
  if not defined GAME if exist "%%L:\SteamLibrary\steam\steamapps\common\Frosthaven\Frosthaven.exe" set "GAME=%%L:\SteamLibrary\steam\steamapps\common\Frosthaven"
)

if defined GAME (
  echo 探测到游戏目录: %GAME%
) else (
  echo 未自动找到游戏目录。
  set /p GAME=请输入游戏目录路径（Frosthaven.exe 所在文件夹）:
)

if not exist "%GAME%\Frosthaven.exe" (
  echo [错误] %GAME% 下没有 Frosthaven.exe，请确认路径。
  pause
  exit /b 1
)

set "SRC=%~dp0"
echo.
echo 即将安装到: %GAME%
echo （BepInEx 5 + FHRelevel 插件；已存在的旧版会被覆盖）
pause

xcopy /e /i /y "%SRC%BepInEx" "%GAME%\BepInEx" >nul || goto :err
copy /y "%SRC%winhttp.dll" "%GAME%\" >nul || goto :err
copy /y "%SRC%doorstop_config.ini" "%GAME%\" >nul || goto :err
if exist "%SRC%.doorstop_version" copy /y "%SRC%.doorstop_version" "%GAME%\" >nul

echo.
echo ============================================
echo  安装完成！
echo  启动游戏时会弹出黑色控制台窗口 = 加载成功
echo  进入战役后 按住 F8 半秒 打开重新升级面板
echo  退出游戏后请等 30 秒以上再重启（防注入失败）
echo ============================================
pause
exit /b 0

:err
echo [错误] 复制文件失败，请检查权限或手动复制。
pause
exit /b 1
