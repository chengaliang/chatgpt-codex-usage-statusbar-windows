Option Explicit

' Launch the dynamic ChatGPT/Codex quota status bar executable.
Dim shell, fileSystem, baseFolder, executableFolder, executablePath
Set fileSystem = CreateObject("Scripting.FileSystemObject")
baseFolder = fileSystem.GetParentFolderName(WScript.ScriptFullName)
executableFolder = fileSystem.GetAbsolutePathName(fileSystem.BuildPath(baseFolder, "..\dist"))
executablePath = fileSystem.BuildPath(executableFolder, "SubscriptionStatus.exe")

Set shell = CreateObject("WScript.Shell")
shell.CurrentDirectory = executableFolder
shell.Run """" & executablePath & """", 1, False
