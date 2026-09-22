param([int]$Minutes = 20)
# Нажимает «ОК» в окне SolidWorks «Формат листа/Размер», пока идёт создание заготовок чертежей.
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Dlg {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
'@
$end = (Get-Date).AddMinutes($Minutes)
$n = 0
while ((Get-Date) -lt $end) {
    $h = [Dlg]::FindWindow('#32770', 'Формат листа/Размер')
    if ($h -ne [IntPtr]::Zero) {
        $ok = [Dlg]::GetDlgItem($h, 1)
        if ($ok -ne [IntPtr]::Zero) { [void][Dlg]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero); $n++; "нажато ОК: $n" }
        Start-Sleep -Milliseconds 1500
    }
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process python -ErrorAction SilentlyContinue)) { break }
}
"готово, нажатий: $n"
