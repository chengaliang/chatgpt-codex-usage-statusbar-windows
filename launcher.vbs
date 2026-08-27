Option Explicit

' Launch the dynamic ChatGPT/Codex quota status bar executable.
Dim shell, baseFolder, executablePath
baseFolder = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
executablePath = baseFolder & "SubscriptionStatus.exe"

Set shell = CreateObject("WScript.Shell")
shell.CurrentDirectory = baseFolder
shell.Run """" & executablePath & """", 1, False
