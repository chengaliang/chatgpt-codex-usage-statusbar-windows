@echo off
set "ROOT=%~dp0"
set "EXE=%ROOT%dist\SubscriptionStatus.exe"
if not exist "%EXE%" (
  echo SubscriptionStatus.exe is missing from dist.
  pause
  exit /b 1
)
pushd "%ROOT%dist"
start "" "SubscriptionStatus.exe"
popd
