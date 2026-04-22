Option Explicit

Dim shell
Dim fso
Dim root
Dim projectPath
Dim runCommand
Dim checkResult

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

root = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root

checkResult = shell.Run("cmd /c where dotnet >nul 2>&1", 0, True)
If checkResult <> 0 Then
    MsgBox "dotnet SDK not found. Please install .NET SDK first.", vbCritical, "Launch Failed"
    WScript.Quit 1
End If

projectPath = """" & fso.BuildPath(root, "Main\Main.csproj") & """"
runCommand = "cmd /c dotnet run --project " & projectPath
shell.Run runCommand, 0, False
