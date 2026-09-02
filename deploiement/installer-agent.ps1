#Requires -Version 5.1
<#
.SYNOPSIS
    Installe l'agent FNE comme service Windows, en s'arrêtant à la moindre
    incertitude.

.DESCRIPTION
    Ce script ne fait rien d'irréversible sans le dire. Il s'exécute en trois
    temps, et chacun peut être joué seul :

      -Preparer     variables machine, registre partagé. Ne crée aucun service.
      -Verifier     un passage de lecture, sans rien envoyer. Ne crée rien.
      -Installer    crée et démarre le service, en mode Manual.

    Sans paramètre, il enchaîne les trois en demandant confirmation avant le
    dernier.

    CE QUI EST EN JEU, et pourquoi ce script existe plutôt qu'une liste de
    commandes à recopier :

    Un service Windows ne tourne pas sous le compte qui l'installe. Deux choses
    que le CLI utilise sans y penser lui échappent - les secrets utilisateur,
    liés au profil, et le registre des certifications, dont le chemin par défaut
    passe par %APPDATA%. Le second est le pire : l'agent tiendrait son registre
    ailleurs que le CLI, chacun ignorant ce que l'autre a envoyé, et la DGI
    recevrait deux fois la même facture. Une facture certifiée ne s'annule pas.

    Le service démarre en mode Manual : il observe et journalise, il n'envoie
    rien. C'est délibéré, et cela doit le rester plusieurs jours.

.NOTES
    Windows refuse par défaut d'exécuter le moindre script. Autorisez-le pour
    cette fenêtre seulement - rien n'est écrit dans le registre Windows, et tout
    revient à la normale à la fermeture :

        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

    Sans -Force, Windows pose une question de confirmation. Si vous avez colle
    plusieurs lignes d'un bloc, les suivantes attendent derriere cette question :
    la console n'est pas figee, elle attend un O.

.EXAMPLE
    .\installer-agent.ps1 -Preparer
    .\installer-agent.ps1 -Verifier
    .\installer-agent.ps1 -Installer
#>

[CmdletBinding()]
param(
    [switch]$Preparer,
    [switch]$Verifier,
    [switch]$Installer,

    # Hors de tout profil utilisateur : c'est tout l'objet.
    [string]$Registre = 'C:\ProgramData\SageFne\certifications.json',
    [string]$Journaux = 'C:\ProgramData\SageFne\journaux',
    [string]$Destination = 'C:\SageFne\agent',
    [string]$NomService = 'SageFneAgent'
)

$ErrorActionPreference = 'Stop'

# Sans paramètre, on fait tout - mais l'installation demandera confirmation.
$tout = -not ($Preparer -or $Verifier -or $Installer)

function Titre($texte) {
    Write-Host ''
    Write-Host $texte -ForegroundColor Cyan
    Write-Host ('-' * $texte.Length) -ForegroundColor Cyan
}

function Note($texte) { Write-Host "  $texte" }
function Bien($texte) { Write-Host "  $texte" -ForegroundColor Green }
function Alerte($texte) { Write-Host "  $texte" -ForegroundColor Yellow }

# Aucun tiret cadratin ni caractère de filet dans ce fichier, et c'est
# délibéré : « - » vaut E2 80 94 en UTF-8, et Windows PowerShell 5.1, qui lit un
# fichier sans BOM en cp1252, y voit le caractère 0x94 - le guillemet fermant
# typographique, qu'il traite comme un vrai délimiteur de chaîne. Les accolades
# se déséquilibrent alors et l'erreur est annoncée cent lignes plus loin.
#
# Le BOM ci-dessus suffit en principe. Mais un BOM se perd - un éditeur, un
# copier-coller, un outil qui réécrit le fichier - et ce script doit survivre à
# cela. Les accents, eux, ne risquent rien : « é » vaut C3 A9, et 0xA9 est « © ».
# Un test du dépôt vérifie les deux.

function EstAdministrateur {
    # Hors de Windows, la question n'a pas de sens et l'appel lève une exception
    # dont le message n'aide personne. Ce script installe un service Windows :
    # le dire est plus utile que de laisser fuir « Windows Principal
    # functionality is not supported on this platform ».
    if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
        throw "Ce script installe un service Windows. Il ne peut rien faire ici."
    }

    try {
        $identite = [Security.Principal.WindowsIdentity]::GetCurrent()
        (New-Object Security.Principal.WindowsPrincipal $identite).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        throw "Impossible de savoir si cette console est administrateur : $_"
    }
}

function RacineDuDepot {
    $dossier = $PSScriptRoot
    while ($dossier -and -not (Test-Path (Join-Path $dossier 'SageFne.sln'))) {
        $dossier = Split-Path $dossier -Parent
    }
    if (-not $dossier) { throw "SageFne.sln introuvable : lancez ce script depuis le dépôt." }
    $dossier
}

# --- Préparation ------------------------------------------------------------

function Preparer-Poste {
    Titre 'Préparation'

    if (-not (EstAdministrateur)) {
        throw "Les variables d'environnement MACHINE demandent une console administrateur. " +
              "Rouvrez PowerShell en tant qu'administrateur."
    }

    $depot = RacineDuDepot

    # 1. Le registre, partagé entre le CLI et le service.
    #
    # C'est l'étape qui touche la seule mémoire de vos certifications. Elle
    # copie, ne déplace pas : l'ancien fichier reste où il est, intact.
    $ancien = Join-Path $env:APPDATA 'SageFne\certifications.json'
    $dossierRegistre = Split-Path $Registre -Parent

    if (-not (Test-Path $dossierRegistre)) {
        New-Item -ItemType Directory -Path $dossierRegistre -Force | Out-Null
        Note "Dossier créé : $dossierRegistre"
    }

    if (Test-Path $Registre) {
        Bien "Registre déjà en place : $Registre"
    }
    elseif (Test-Path $ancien) {
        Copy-Item $ancien $Registre
        Bien "Registre copié depuis votre profil vers $Registre"
        Alerte "L'ancien reste en place, intact : $ancien"
        Alerte "Ne l'effacez pas avant d'avoir vérifié que le nouveau porte bien vos certifications."
    }
    else {
        Alerte "Aucun registre existant. Un nouveau sera créé au premier envoi."
    }

    if (-not (Test-Path $Journaux)) {
        New-Item -ItemType Directory -Path $Journaux -Force | Out-Null
    }

    # 2. Les variables MACHINE. Elles servent le CLI comme le service : un seul
    #    registre pour les deux, ce qui est exactement le but.
    Titre 'Variables machine'

    $aPoser = [ordered]@{
        'Fne__CertificationLedgerPath' = $Registre
        'Agent__CheminJournal'         = $Journaux
        'Agent__FenetreJours'          = '30'
        'Agent__Mode'                  = 'Manual'
    }

    foreach ($nom in $aPoser.Keys) {
        [Environment]::SetEnvironmentVariable($nom, $aPoser[$nom], 'Machine')
        Set-Item "env:$nom" $aPoser[$nom]
        Note "$nom = $($aPoser[$nom])"
    }

    # 3. Les secrets. Repris des secrets utilisateur du CLI s'ils s'y trouvent,
    #    sans jamais être affichés.
    Titre 'Secrets'

    # L'adresse de la plateforme n'est pas un secret : elle a une valeur par
    # défaut, celle que la DGI publie pour son environnement d'essai. Sans elle,
    # la sonde réseau répond « injoignable » et l'agent n'enverrait rien même
    # une fois passé en Automatic.
    if (-not [Environment]::GetEnvironmentVariable('Fne__BaseUrl', 'Machine')) {
        [Environment]::SetEnvironmentVariable('Fne__BaseUrl', 'http://54.247.95.108/ws', 'Machine')
        Set-Item 'env:Fne__BaseUrl' 'http://54.247.95.108/ws'
        Alerte "Fne__BaseUrl posée sur la plateforme d'ESSAI de la DGI."
        Alerte "Cette adresse est en HTTP clair : n'y mettez jamais une clé de production."
    }

    foreach ($secret in @(
        @{ Cle = 'ConnectionStrings:Sage'; Variable = 'ConnectionStrings__Sage'; Nom = 'chaîne de connexion Sage' },
        @{ Cle = 'Fne:ApiKey';             Variable = 'Fne__ApiKey';             Nom = "clé d'API FNE" }
    )) {
        $existant = [Environment]::GetEnvironmentVariable($secret.Variable, 'Machine')
        if ($existant) {
            Bien "$($secret.Nom) : déjà posée en variable machine."
            Set-Item "env:$($secret.Variable)" $existant
            continue
        }

        # « dotnet » peut manquer du PATH d'une console administrateur : c'est
        # une gêne, pas une raison d'abandonner l'installation.
        $depuisSecrets = $null
        try {
            $depuisSecrets = & dotnet user-secrets list --project (Join-Path $depot 'src\SageFne.Reader') 2>$null |
                Select-String "^$([regex]::Escape($secret.Cle)) = " |
                ForEach-Object { $_.Line -replace "^$([regex]::Escape($secret.Cle)) = " }
        }
        catch {
            Alerte "Secrets du CLI illisibles ici (dotnet absent du PATH administrateur ?)."
        }

        if ($depuisSecrets) {
            [Environment]::SetEnvironmentVariable($secret.Variable, $depuisSecrets, 'Machine')
            Set-Item "env:$($secret.Variable)" $depuisSecrets
            Bien "$($secret.Nom) : reprise des secrets du CLI, sans être affichée."
        }
        else {
            Alerte "$($secret.Nom) : absente. Posez-la vous-même -"
            Alerte "  [Environment]::SetEnvironmentVariable('$($secret.Variable)', '…', 'Machine')"
        }
    }

    # 4. Le binaire.
    Titre 'Publication'
    & dotnet publish (Join-Path $depot 'src\SageFne.Agent') -c Release -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }
    Bien "Publié dans $Destination"
}

# --- Vérification -----------------------------------------------------------

function Verifier-Poste {
    Titre 'Vérification'
    Note "Un passage de lecture. Aucun service n'est créé, aucune facture n'est envoyée."

    $exe = Join-Path $Destination 'SageFne.Agent.exe'
    if (-not (Test-Path $exe)) { throw "$exe introuvable. Lancez d'abord -Preparer." }

    # L'agent doit écrire là où ce script ira lire. Sans ces variables, il
    # retombe sur son %APPDATA% par défaut, le script cherche ailleurs, ne
    # trouve rien - et croit que rien n'a été écrit.
    $env:Agent__CheminJournal = $Journaux
    $env:Fne__CertificationLedgerPath = $Registre
    if (-not (Test-Path $Journaux)) {
        New-Item -ItemType Directory -Path $Journaux -Force | Out-Null
    }

    $journal = Join-Path $Journaux "agent-$(Get-Date -Format 'yyyy-MM-dd').log"
    $avant = if (Test-Path $journal) { (Get-Item $journal).Length } else { -1 }

    # Start-Process -Wait : le binaire est compilé sans console, PowerShell ne
    # l'attendrait pas et l'on lirait un journal encore vide.
    $processus = Start-Process $exe -ArgumentList '--verifier' -Wait -PassThru -NoNewWindow

    $apres = if (Test-Path $journal) { (Get-Item $journal).Length } else { -1 }

    if (Test-Path $journal) {
        Titre 'Journal'
        Get-Content $journal -Encoding UTF8 | Select-Object -Last 25
    }

    # Un journal absent ou inchangé n'est pas une vérification qui passe : c'est
    # une vérification dont on ne sait rien. Les confondre serait déclarer bon
    # ce qu'on n'a pas regardé - la faute que tout ce projet s'emploie à éviter.
    if ($apres -le $avant) {
        throw "L'agent n'a rien écrit dans $Journaux. Sans journal, cette vérification " +
              "ne prouve rien - ne la prenez pas pour un succès. Vérifiez que le dossier " +
              "est accessible en écriture, puis relancez."
    }

    if ($processus.ExitCode -ne 0) {
        throw "La vérification a échoué (code $($processus.ExitCode)). Le journal ci-dessus dit " +
              "pourquoi. N'installez pas le service tant que ce n'est pas réglé."
    }

    Bien "Vérification passée, et le journal le montre."
    return $true
}

# --- Installation -----------------------------------------------------------

function Installer-Service {
    Titre 'Installation du service'

    if (-not (EstAdministrateur)) {
        throw "La création d'un service demande une console administrateur."
    }

    $exe = Join-Path $Destination 'SageFne.Agent.exe'
    if (-not (Test-Path $exe)) { throw "$exe introuvable. Lancez d'abord -Preparer." }

    if (Get-Service $NomService -ErrorAction SilentlyContinue) {
        Alerte "Le service $NomService existe déjà. Arrêt et suppression avant recréation."
        & sc.exe stop $NomService | Out-Null
        Start-Sleep -Seconds 2
        & sc.exe delete $NomService | Out-Null
        Start-Sleep -Seconds 2
    }

    & sc.exe create $NomService binPath= "`"$exe`"" start= auto DisplayName= "SageFne Agent" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "La création du service a échoué." }

    & sc.exe description $NomService "Certification FNE des factures Sage. Mode Manual : observe sans envoyer." | Out-Null

    # Redémarrage automatique après incident, mais pas en boucle : un agent qui
    # redémarre toutes les secondes noie son journal et n'avance à rien.
    & sc.exe failure $NomService reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Null

    & sc.exe start $NomService | Out-Null
    Start-Sleep -Seconds 3

    $service = Get-Service $NomService
    if ($service.Status -ne 'Running') {
        throw "Le service ne tourne pas (état : $($service.Status)). Lisez $Journaux."
    }

    Bien "Service $NomService démarré, en mode Manual."
    Note "Il observe et journalise. Il n'enverra rien tant que Agent__Mode vaut Manual."
    Note ""
    Note "Pour le suivre :"
    Note "  Get-Content '$Journaux\agent-$(Get-Date -Format 'yyyy-MM-dd').log' -Wait -Encoding UTF8"
    Note ""
    Note "Pour l'arrêter :"
    Note "  sc.exe stop $NomService"
}

# --- Enchaînement -----------------------------------------------------------

try {
    if ($Preparer -or $tout) { Preparer-Poste }
    if ($Verifier -or $tout) { Verifier-Poste | Out-Null }

    if ($Installer) {
        Installer-Service
    }
    elseif ($tout) {
        Titre 'Dernière étape'
        Alerte "La vérification est passée. L'installation crée un service Windows qui démarrera"
        Alerte "avec la machine et tournera sans session ouverte."
        Write-Host ''
        $reponse = Read-Host "  Installer le service $NomService maintenant ? (oui/non)"
        if ($reponse -eq 'oui') {
            Installer-Service
        }
        else {
            Note "Rien n'a été installé. Relancez avec -Installer quand vous voudrez."
        }
    }
}
catch {
    Write-Host ''
    Write-Host "  ARRÊT : $_" -ForegroundColor Red
    Write-Host ''
    exit 1
}
