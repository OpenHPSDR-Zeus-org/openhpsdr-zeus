' Launch the Zeus desktop build with NO visible console window.
'
' launch-zeus-desktop.cmd does the real work (pick the develop build, kill any
' running instance, wait for it to release port 6061, then launch the GUI exe).
' Run from a shortcut, that .cmd opens a cmd.exe console -- and on Windows 11 the
' default console host is Windows Terminal, so it shows up as a transparent
' (acrylic) terminal window flashing behind the Zeus frame.
'
' WScript.Shell.Run with intWindowStyle = 0 runs the same .cmd with a hidden
' window: no console flash, no taskbar button. The Zeus exe is a GUI-subsystem
' binary (WinExe), so its own window still appears normally -- only the cmd
' console is suppressed. Point the "Zeus" desktop shortcut at:
'   wscript.exe "<repo>\scripts\launch-zeus-desktop-hidden.vbs"
Dim sh, scriptDir
Set sh = CreateObject("WScript.Shell")
scriptDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
' 0 = hidden window, False = don't wait for the .cmd to finish.
sh.Run "cmd /c """ & scriptDir & "launch-zeus-desktop.cmd""", 0, False
