param([string]$GameDir = "")

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$modName = "PerformanceTuner"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-DataDir($p) {
    if (-not $p -or -not (Test-Path $p)) { return $null }
    foreach ($d in Get-ChildItem $p -Directory -Filter "*_Data" -ErrorAction SilentlyContinue) {
        if (Test-Path (Join-Path $d.FullName "Managed\Assembly-CSharp.dll")) { return $d.FullName }
    }
    return $null
}

function Test-GameDir($p) {
    return [bool](Get-DataDir $p)
}

function Find-GameDir {
    $cands = @()
    # GOG
    foreach ($root in @("HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games", "HKLM:\SOFTWARE\GOG.com\Games")) {
        try {
            foreach ($k in Get-ChildItem $root -ErrorAction Stop) {
                $props = Get-ItemProperty $k.PSPath
                if ($props.gameName -like "*Villeins*" -and $props.path) { $cands += $props.path }
            }
        } catch {}
    }
    $cands += "C:\GOG Games\Lords and Villeins"
    $cands += "D:\GOG Games\Lords and Villeins"
    # Steam
    $steam = $null
    try { $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction Stop).SteamPath } catch {}
    $libs = @()
    if ($steam) {
        $steam = $steam -replace "/", "\"
        $libs += $steam
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') { $libs += ($Matches[1] -replace '\\', '\') }
            }
        }
    }
    $libs += "C:\Program Files (x86)\Steam"
    foreach ($l in $libs) { $cands += (Join-Path $l "steamapps\common\Lords and Villeins") }
    foreach ($c in $cands) { if (Test-GameDir $c) { return $c } }
    return $null
}

Write-Host ""
Write-Host "=== Performance Tuner: сборка и установка ===" -ForegroundColor Cyan

# 1. Папка игры
if (-not (Test-GameDir $GameDir)) { $GameDir = Find-GameDir }
if (-not (Test-GameDir $GameDir)) {
    Write-Host "Папка игры не найдена автоматически. Выберите папку, где лежит exe-файл Lords and Villeins."
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Выберите папку игры Lords and Villeins (где лежит exe и папка Lords and Villeins_Data)"
    if ($dlg.ShowDialog() -eq "OK") { $GameDir = $dlg.SelectedPath }
}
if (-not (Test-GameDir $GameDir)) { throw "Это не папка игры: внутри не найдена папка *_Data\Managed\Assembly-CSharp.dll" }
Write-Host "Папка игры: $GameDir"
$managed = Join-Path (Get-DataDir $GameDir) "Managed"

# 2. Unity Mod Manager и Harmony
$umm = Get-ChildItem -Path $GameDir -Recurse -Filter "UnityModManager.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $umm) {
    throw "Не найден UnityModManager.dll. Сначала установите Unity Mod Manager в игру (ссылка есть на странице мода Royalty Manager в разделе 'Требования'), затем запустите этот файл снова."
}
$harmony = Get-ChildItem -Path $umm.DirectoryName -Filter "0Harmony.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $harmony) { $harmony = Get-ChildItem -Path $GameDir -Recurse -Filter "0Harmony.dll" -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $harmony) { throw "Не найден 0Harmony.dll рядом с Unity Mod Manager." }
Write-Host "Unity Mod Manager: $($umm.FullName)"

# 3. Компилятор C#, встроенный в Windows
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "Не найден csc.exe (.NET Framework 4). Установите .NET Framework 4.8 или .NET SDK." }

# 4. Ссылки на сборки игры
$refs = New-Object System.Collections.Generic.List[string]
$extra = @("Assembly-CSharp.dll", "OdinSerializer.dll", "GraphAndChart.dll", "LoopScrollRect.Runtime.dll", "ProjectDawn.Collections.dll", "Logger.dll")
foreach ($f in Get-ChildItem $managed -Filter *.dll) {
    $n = $f.Name
    if (($n -like "UnityEngine*") -or ($n -like "Unity.*") -or ($extra -contains $n)) {
        try { [void][System.Reflection.AssemblyName]::GetAssemblyName($f.FullName); $refs.Add($f.FullName) } catch {}
    }
}
$refs.Add($umm.FullName)
$refs.Add($harmony.FullName)

# 5. Сборка через response-файл
$work = Join-Path $env:TEMP "$modName-build"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
$outDll = Join-Path $work "$modName.dll"
$rsp = Join-Path $work "build.rsp"
$lines = @("/nologo", "/target:library", "/optimize+", "/codepage:65001", "/nowarn:1701,1702", "/out:`"$outDll`"")
foreach ($r in $refs) { $lines += "/r:`"$r`"" }
$lines += "`"$(Join-Path $here 'Main.cs')`""
[System.IO.File]::WriteAllLines($rsp, $lines, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Компиляция..."
& $csc "@$rsp"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outDll)) {
    throw "Ошибка компиляции (текст выше). Скопируйте его и пришлите мне."
}

# 6. Установка в Mods
$modsDir = Join-Path $GameDir "Mods"
$target = Join-Path $modsDir $modName
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item $outDll $target -Force
Copy-Item (Join-Path $here "Info.json") $target -Force

Write-Host ""
Write-Host "Готово! Мод установлен в: $target" -ForegroundColor Green
Write-Host "Запустите игру, нажмите Ctrl+F10 и откройте вкладку Performance Tuner."
