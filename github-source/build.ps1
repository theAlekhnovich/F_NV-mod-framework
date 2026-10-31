# Build the installer EXE. Needs the .NET Framework 4 C# compiler that ships with Windows (no SDK).
# Usage: .\build.ps1 -Package path\to\package.json [-Out .\release]
param([Parameter(Mandatory=$true)][string]$Package,[string]$Out="$PSScriptRoot\release")
$R=$PSScriptRoot
$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
foreach($a in 'Lucky38.ico','Logo.png','cat-cowboy-cutout.png'){ if(-not (Test-Path "$R\assets\$a")){ throw "Missing assets\$a - see assets\README.md" } }
New-Item -ItemType Directory -Force $Out | Out-Null
$src=@('AssemblyInfo','Installer','Integrations','Wizard','LanguageSupport','Prerequisites','LaunchSupport','DefenderSupport','UninstallSupport','PortableSupport','Localization','UpdateSupport','CatEncounter')|%{"$R\src\$_.cs"}
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
  "/win32manifest:$R\src\app.manifest" "/win32icon:$R\assets\Lucky38.ico" `
  "/resource:$R\assets\Lucky38.ico,Lucky38.ico" "/resource:$R\assets\Logo.png,Logo.png" "/resource:$R\assets\cat-cowboy-cutout.png,cat-cowboy-cutout.png" `
  "/resource:$Package,package.json" "/resource:$R\src\prerequisites.json,prerequisites.json" `
  "/resource:$R\src\english-packs.json,english-packs.json" `
  "/out:$Out\Fallout Mod Setup.exe" $src
if($LASTEXITCODE -ne 0){ throw "csc failed" }
"Built: $Out\Fallout Mod Setup.exe"
