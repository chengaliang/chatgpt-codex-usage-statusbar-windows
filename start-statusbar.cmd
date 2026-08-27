@echo off
cd /d "%~dp0"
if not exist "%~dp0SubscriptionStatus.exe" (
  echo SubscriptionStatus.exe is missing.
  pause
  exit /b 1
)
start "" "%~dp0SubscriptionStatus.exe"
