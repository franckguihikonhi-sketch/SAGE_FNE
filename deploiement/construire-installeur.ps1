# Produit SageFneSetup.exe : un fichier, autonome, qui porte l'agent en lui.
#
# Trois etapes, dans cet ordre, et l'ordre compte : l'agent doit etre publie
# avant d'etre compresse, et compresse avant que l'installeur ne l'embarque.
#
# Aucun tiret cadratin ni caractere de filet dans ce fichier : Windows
# PowerShell 5.1 lit un .ps1 sans BOM en cp1252 et prend l'octet 0x94 pour un
# guillemet fermant, ce qui desequilibre les accolades cent lignes plus loin.

param(
    [string]$Sortie = 'publication',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$racine = Split-Path -Parent $PSScriptRoot
$installeur = Join-Path $racine 'src\SageFne.Installeur'
$zip = Join-Path $installeur 'agent.zip'

function Etape($texte) {
    Write-Host ''
    Write-Host $texte
    Write-Host ('-' * $texte.Length)
}

Etape 'Agent'

$agent = Join-Path $env:TEMP "sagefne-agent-$(Get-Random)"

# Autonome : le poste d'un client n'a pas le runtime .NET, et lui demander de
# l'installer serait une etape de plus a rater. Pas de PublishSingleFile ici :
# le service tourne mieux en fichiers separes, et c'est l'installeur qui doit
# etre unique, pas l'agent.
dotnet publish (Join-Path $racine 'src\SageFne.Agent') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $agent
if ($LASTEXITCODE -ne 0) { throw "La publication de l'agent a echoue." }

Write-Host "  Publie dans $agent"

Etape 'Charge utile'

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $agent '*') -DestinationPath $zip -CompressionLevel Optimal
$poids = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "  agent.zip : $poids Mo"

Etape 'Installeur'

$arguments = @(
    'publish', $installeur,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '--output', (Join-Path $racine $Sortie)
)

if ($Version) { $arguments += "-p:Version=$Version" }

dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "La publication de l'installeur a echoue." }

Remove-Item $agent -Recurse -Force
Remove-Item $zip -Force

$exe = Join-Path (Join-Path $racine $Sortie) 'SageFneSetup.exe'
$taille = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Etape 'Pret'
Write-Host "  $exe"
Write-Host "  $taille Mo, un seul fichier, aucun prerequis sur le poste du client."
Write-Host ''
Write-Host '  A donner tel quel. Sur le poste, clic droit puis'
Write-Host '  « Executer en tant qu''administrateur ».'
