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
    Windows refuse par défaut d'exécuter le moindre script. Autorisez-le pour ce
    seul lancement - rien n'est écrit dans le registre Windows, et aucune
    question n'est posée :

        powershell -ExecutionPolicy Bypass -File .\deploiement\installer-agent.ps1 -Preparer

    Set-ExecutionPolicy -Scope Process fonctionne aussi, mais ne vaut que pour
    la fenêtre courante : il faut la retaper à chaque console rouverte.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer-agent.ps1 -Preparer
    powershell -ExecutionPolicy Bypass -File .\installer-agent.ps1 -Verifier
    powershell -ExecutionPolicy Bypass -File .\installer-agent.ps1 -Installer
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
    [string]$NomService = 'SageFneAgent',

    # Le jour a partir duquel le middleware se sent concerne. Rien de date
    # avant lui ne sera jamais candidat.
    #
    # Vide par defaut, et c'est voulu : la date vit dans appsettings.json, sous
    # Fne:DemarrageLe, ou elle est versionnee et suivie par le CLI comme par
    # l'agent. Une variable machine posee ici la remplacerait en silence, et
    # l'on aurait deux dates pour une seule frontiere sans savoir laquelle
    # s'applique. Ne renseignez ce parametre que pour deroger sciemment sur ce
    # poste.
    [string]$DemarrageLe = '',

    # Manual : l'agent observe et journalise, il n'envoie rien.
    # Automatic : les factures conformes et stables partent d'elles-mêmes.
    #
    # Vide par defaut : -Preparer ne touche pas au mode deja pose. Sans cela,
    # une preparation de routine ramenerait silencieusement un poste d'Automatic
    # a Manual, et l'on chercherait longtemps pourquoi plus rien ne part.
    [ValidateSet('', 'Manual', 'Automatic')]
    [string]$Mode = '',

    # L'identite du contribuable aupres de la DGI. Elle ne vient pas de Sage :
    # la DGI la donne avec l'acces a la plateforme.
    # La base d'audit du SaaS. Non secrets, contrairement a la cle de service :
    # ils vivent dans appsettings.json comme le reste du parametrage. Vides, le
    # miroir reste eteint et la certification ne change pas d'un octet.
    [string]$SupabaseUrl = '',
    [string]$Dossier = '',

    [string]$PointDeVente = '',
    [string]$Etablissement = '',

    # Minutes pendant lesquelles une piece doit rester inchangee avant de
    # partir. Le compteur repart de zero a chaque modification : le delai ne
    # couvre donc pas la duree de la saisie, mais la pause qui la suit.
    #
    # Trop court, une pause en cours de saisie - un appel telephonique - passe
    # pour une saisie finie et la facture part incomplete. Trop long, elle se
    # fait attendre. Vide : la valeur d'appsettings.json est conservee.
    [int]$Stabilite = 0,

    # Sur combien de jours en arriere l'agent regarde a chaque tour. Vide : la
    # valeur en place est conservee, et a defaut celle d'appsettings.json.
    [int]$Fenetre = 0
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

# Le meme motif que SageFne.Core.Validation.MarqueurGabarit. Un exemple de la
# documentation recopie tel quel dans une commande est arrive quatre fois ; la
# derniere, « VOTRE_ETAB » s'est installe dans l'identite du dossier aupres de la
# DGI, qui refusait alors toutes les factures sans que rien n'avertisse - la
# valeur n'etait reconnue comme un trou par aucune des listes.
$script:MotifGabarit =
    '^(A_COMPLETER|A_RENSEIGNER|A_DEFINIR|TODO|XXXX?|EXEMPLE|PLACEHOLDER|CHANGEME' +
    '|LA_REFERENCE|TA_REFERENCE_FNE|REFERENCE|REF|LE_NUMERO|NUMERO|MOT_DE_PASSE)$' +
    '|^(VOTRE_|VOS_|MON_|MA_|MES_|TON_|TES_|YOUR_|MY_|EXEMPLE_|SAMPLE_)' +
    '|[<>\u2026\u00AB\u00BB]'

function Fixer-Propriete($objet, [string]$nom, $valeur) {
    # ConvertFrom-Json rend un PSCustomObject : y poser une propriete absente
    # leve une exception. C'est ainsi que -Preparer s'est interrompu apres avoir
    # efface les variables machine, laissant le poste sans reglage d'aucun cote.
    #
    # Un objet nul - une section entiere manquante - fait echouer Add-Member sur
    # un InputObject vide, et le script s'arrete avant d'ecrire quoi que ce soit.
    # Le dire clairement vaut mieux qu'une trace de liaison de parametre.
    if ($null -eq $objet) {
        throw "La section attendue est absente d'appsettings.json : impossible d'y poser " +
              "« $nom ». Le fichier publie est incomplet - republiez, ou restaurez-le depuis " +
              "src\SageFne.Agent\appsettings.json."
    }

    if ($objet.PSObject.Properties.Name -contains $nom) { $objet.$nom = $valeur }
    else { $objet | Add-Member -NotePropertyName $nom -NotePropertyValue $valeur }
}

function Redemarrer-Service {
    & sc.exe start $NomService 2>&1 | Out-Null

    # Un service qui lit Sage au demarrage met plus de deux secondes a passer
    # Running. Attendre un temps fixe faisait annoncer un echec sur un service
    # qui demarrait tres bien.
    $attendu = 0
    while ($attendu -lt 60) {
        $etat = (Get-Service $NomService -ErrorAction SilentlyContinue).Status
        if ($etat -eq 'Running') {
            Note "Service redemarre apres $attendu s."
            return $true
        }
        if ($etat -eq 'Stopped' -and $attendu -ge 5) { break }
        Start-Sleep -Seconds 1
        $attendu++
    }

    Alerte "ATTENTION : le service ne redemarre pas apres $attendu s. Relancez-le a la main :"
    Alerte "  sc.exe start $NomService"
    Alerte "puis lisez le journal - le garde-fou d'installation y dit ce qu'il refuse."
    return $false
}

function Preparer-Poste {
    Titre 'Préparation'

    if (-not (EstAdministrateur)) {
        # Dire ce qui exige les droits, et comment rouvrir une console qui les a.
        # « Rouvrez en administrateur » sans plus laisse chercher : le repertoire
        # de depart C:\WINDOWS\System32 ressemble a une console elevee sans en
        # etre une, et l'on relance la meme commande en croyant avoir obei.
        throw "Cette preparation demande une console administrateur. Elle arrete et " +
              "redemarre le service, ecrit dans $Destination et $Registre, et retire " +
              "des variables d'environnement MACHINE.`n`n" +
              "Pour ouvrir une console elevee deja placee ici, collez cette ligne :`n" +
              "  Start-Process powershell -Verb RunAs -ArgumentList '-NoExit','-Command'," +
              "'cd ''$PWD'''`n`n" +
              "Windows demandera confirmation. La nouvelle fenetre reste ouverte : " +
              "relancez-y la meme commande."
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

    # 2. Les secrets. Repris des secrets utilisateur du CLI s'ils s'y trouvent,
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
        @{ Cle = 'Fne:ApiKey';             Variable = 'Fne__ApiKey';             Nom = "clé d'API FNE" },

        # Facultative : sans elle le miroir vers la base d'audit reste eteint,
        # et l'agent certifie exactement comme avant. Elle donne un acces
        # complet a la base : meme traitement que la cle FNE, variable machine
        # et jamais appsettings.json.
        @{ Cle = 'Saas:CleService';        Variable = 'Saas__CleService';        Nom = "clé de la base d'audit"; Facultatif = $true }
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
        elseif ($secret.Facultatif) {
            Note "$($secret.Nom) : absente. Le miroir vers la base d'audit reste eteint,"
            Note "  et rien ne change pour la certification. Pour l'allumer :"
            Note "    -Preparer -SupabaseUrl 'https://xxxx.supabase.co' -Dossier '<uuid>'"
            Note "  puis posez la cle : [Environment]::SetEnvironmentVariable('Saas__CleService', '...', 'Machine')"
        }
        else {
            Alerte "$($secret.Nom) : absente. Posez-la vous-même -"
            Alerte "  [Environment]::SetEnvironmentVariable('$($secret.Variable)', '…', 'Machine')"
        }
    }

    # L'identite du dossier aupres de la DGI. Elle vit dans les secrets du CLI
    # - c'est la que la documentation dit de la poser - et le service, qui ne
    # tourne pas sous ce compte, ne l'y voit pas. Il retombait donc sur
    # « A_COMPLETER » d'appsettings.json et la DGI refusait TOUTES les factures :
    # « Establishment is invalid ». Le CLI certifiait au meme moment, avec la
    # bonne valeur, depuis le meme depot.
    #
    # Ces deux-la ne sont pas des secrets : ils figurent sur chaque facture
    # certifiee. Ils vont donc dans appsettings.json, et non en variable machine.
    $identiteReprise = @{}
    foreach ($cle in @('Fne:PointOfSale', 'Fne:Establishment')) {
        try {
            $valeur = & dotnet user-secrets list --project (Join-Path $depot 'src\SageFne.Reader') 2>$null |
                Select-String "^$([regex]::Escape($cle)) = " |
                ForEach-Object { $_.Line -replace "^$([regex]::Escape($cle)) = " }

            if ($valeur -and $valeur -notmatch $MotifGabarit) {
                $identiteReprise[$cle.Split(':')[1]] = $valeur
            }
        }
        catch {
            # dotnet hors du PATH administrateur : on le dira plus bas, quand
            # les reglages seront relus et l'identite affichee.
        }
    }

    # 3. Le binaire.
    Titre 'Publication'

    # Le service verrouille ses propres DLL. Sans cet arret, dotnet publish
    # echoue apres dix tentatives sur « le fichier est verrouille par SageFne
    # Agent », et l'on repart avec l'ancien binaire en croyant avoir la nouvelle
    # version - la mise a jour la plus dangereuse qui soit : celle qu'on croit
    # faite.
    # Ce que le poste applique aujourd'hui, lu AVANT que dotnet publish ne
    # reecrive appsettings.json avec les valeurs versionnees. Sans cette
    # memoire, un -Preparer de routine ramenerait le mode a Manual et le delai
    # a sa valeur d'origine : une mise a jour desactiverait l'automatisme sans
    # que personne ne l'ait demande. Le meme piege que les variables machine
    # reecrites a chaque preparation, deplace dans le fichier.
    #
    # La section Fne est reprise en entier, et non champ par champ : c'est la
    # troisieme fois qu'un reglage disparait parce qu'il n'etait pas dans la
    # liste de ceux qu'on pense a porter. PointOfSale et Establishment
    # identifient le contribuable aupres de la DGI ; remis a « A_COMPLETER » par
    # une republication, ils font refuser toutes les factures avec
    # « Establishment is invalid » - et rien, dans Sage, ne peut le prevoir.
    $fichierAvant = Join-Path $Destination 'appsettings.json'
    $modeEnPlace = $null
    $stabiliteEnPlace = $null
    $fenetreEnPlace = $null
    $fneEnPlace = $null
    $saasEnPlace = $null
    if (Test-Path $fichierAvant) {
        $ancien = Get-Content $fichierAvant -Raw -Encoding UTF8 | ConvertFrom-Json
        $modeEnPlace = $ancien.Agent.Mode
        $stabiliteEnPlace = $ancien.Agent.StabiliteMinutes
        $fenetreEnPlace = $ancien.Agent.FenetreJours
        $fneEnPlace = $ancien.Fne
        $saasEnPlace = $ancien.Saas
    }

    $service = Get-Service $NomService -ErrorAction SilentlyContinue
    $tournait = $service -and $service.Status -eq 'Running'

    if ($tournait) {
        Alerte "Le service $NomService tourne et verrouille ses fichiers. Arret le temps de"
        Alerte "publier, puis redemarrage. Pendant ces quelques secondes, aucune facture"
        Alerte "n'est examinee ni envoyee."
        & sc.exe stop $NomService 2>&1 | Out-Null

        $attendu = 0
        while ((Get-Service $NomService).Status -ne 'Stopped' -and $attendu -lt 60) {
            Start-Sleep -Seconds 1
            $attendu++
        }

        if ((Get-Service $NomService).Status -ne 'Stopped') {
            throw "Le service $NomService ne s'arrete pas apres $attendu s. Rien n'a ete " +
                  "publie : l'ancien binaire reste en place, ce qui vaut mieux qu'une " +
                  "publication a moitie faite."
        }

        Note "Service arrete."
    }

    & dotnet publish (Join-Path $depot 'src\SageFne.Agent') -c Release -o $Destination
    $publication = $LASTEXITCODE

    if ($publication -ne 0) {
        # Le service reprend avant qu'on ne s'arrete : arrete par ce script, il
        # ne doit pas le rester parce que la compilation s'est mal passee.
        # L'ancien binaire est intact, ses reglages aussi.
        if ($tournait) { Redemarrer-Service }
        throw "La publication a échoué. L'ancien binaire reste en place."
    }

    Bien "Publié dans $Destination"

    # Les reglages non secrets vont dans l'appsettings.json publie, apres la
    # publication qui vient de le reecrire. Le service le lit a coup sur, la ou
    # une variable machine posee apres l'amorcage de Windows peut lui rester
    # invisible.
    #
    # Les secrets n'y entrent pas : chaine de connexion et cle d'API restent en
    # variables machine, hors de tout fichier.
    Titre 'Reglages du service'
    $fichier = Join-Path $Destination 'appsettings.json'
    if (-not (Test-Path $fichier)) {
        throw "$fichier introuvable apres publication. Rien n'a ete regle."
    }

    $config = Get-Content $fichier -Raw -Encoding UTF8 | ConvertFrom-Json

    # Poser une propriete que l'objet ne porte pas encore leve une exception :
    # l'appsettings de l'agent n'avait pas de CertificationLedgerPath, et le
    # script s'est interrompu APRES avoir efface les variables machine. Le poste
    # s'est retrouve sans reglage d'aucun cote. Fixer-Propriete l'ajoute au lieu
    # d'echouer.
    Fixer-Propriete $config.Agent 'CheminJournal' $Journaux
    Fixer-Propriete $config.Fne 'CertificationLedgerPath' $Registre

    # Le parametre passe devant ; a defaut, ce que le poste appliquait deja ;
    # a defaut encore, la valeur versionnee, qui est Manual.
    if ($Mode) { Fixer-Propriete $config.Agent 'Mode' $Mode }
    elseif ($modeEnPlace) { Fixer-Propriete $config.Agent 'Mode' $modeEnPlace }

    if ($Stabilite -gt 0) { Fixer-Propriete $config.Agent 'StabiliteMinutes' $Stabilite }
    elseif ($null -ne $stabiliteEnPlace) { Fixer-Propriete $config.Agent 'StabiliteMinutes' $stabiliteEnPlace }

    # FenetreJours vivait dans une variable machine, retiree avec les autres, et
    # n'etait ecrite nulle part : elle est retombee de 30 a 7 sans un mot. Un
    # reglage qu'on cesse de porter sans le dire est un reglage perdu.
    if ($Fenetre -gt 0) { Fixer-Propriete $config.Agent 'FenetreJours' $Fenetre }
    elseif ($null -ne $fenetreEnPlace) { Fixer-Propriete $config.Agent 'FenetreJours' $fenetreEnPlace }

    # Tout ce que la section Fne portait est repose, sauf les deux chemins que
    # cette preparation vient justement de fixer. Une valeur non vide et non
    # gabarit l'emporte sur celle du depot : c'est un reglage de ce poste, que
    # la version livree ne connait pas.
    if ($null -ne $fneEnPlace) {
        foreach ($champ in $fneEnPlace.PSObject.Properties) {
            if ($champ.Name -in @('CertificationLedgerPath')) { continue }

            $valeur = $champ.Value
            if ($null -eq $valeur) { continue }
            if ($valeur -is [string]) {
                if ([string]::IsNullOrWhiteSpace($valeur)) { continue }
                if ($valeur -match $MotifGabarit) { continue }
            }

            Fixer-Propriete $config.Fne $champ.Name $valeur
        }
    }

    # Puis ce que les secrets du CLI portaient : c'est la valeur avec laquelle
    # des factures ont deja ete certifiees.
    foreach ($nom in $identiteReprise.Keys) {
        $dejaBonne = $config.Fne.PSObject.Properties.Name -contains $nom -and
                     $config.Fne.$nom -and
                     $config.Fne.$nom -notmatch $MotifGabarit

        if (-not $dejaBonne) {
            Fixer-Propriete $config.Fne $nom $identiteReprise[$nom]
            Bien "Fne:$nom repris des secrets du CLI."
        }
    }

    # Le parametre passe devant tout : c'est la correction qu'on vient taper.
    # La section Saas est reprise comme la section Fne, et pour la meme raison :
    # un reglage qu'on cesse de porter est un reglage perdu. FenetreJours est
    # deja retombe de 30 a 7 de cette facon, et l'identite du dossier a
    # « A_COMPLETER ».
    if ($null -ne $saasEnPlace) {
        if (-not $config.PSObject.Properties['Saas']) {
            $config | Add-Member -NotePropertyName 'Saas' -NotePropertyValue ([pscustomobject]@{})
        }

        foreach ($champ in $saasEnPlace.PSObject.Properties) {
            $valeur = $champ.Value
            if ($null -eq $valeur) { continue }
            if ($valeur -is [string]) {
                if ([string]::IsNullOrWhiteSpace($valeur)) { continue }
                if ($valeur -match $MotifGabarit) { continue }
            }

            Fixer-Propriete $config.Saas $champ.Name $valeur
        }
    }

    if ($SupabaseUrl -or $Dossier) {
        if (-not $config.PSObject.Properties['Saas']) {
            $config | Add-Member -NotePropertyName 'Saas' -NotePropertyValue ([pscustomobject]@{})
        }

        if ($SupabaseUrl) { Fixer-Propriete $config.Saas 'Url' $SupabaseUrl }
        if ($Dossier)     { Fixer-Propriete $config.Saas 'DossierId' $Dossier }
    }

    if ($PointDeVente) { Fixer-Propriete $config.Fne 'PointOfSale' $PointDeVente }
    if ($Etablissement) { Fixer-Propriete $config.Fne 'Establishment' $Etablissement }

    $config | ConvertTo-Json -Depth 10 | Set-Content $fichier -Encoding UTF8

    # Relu depuis le fichier, pas depuis les variables : afficher ce qu'on
    # vient d'ecrire sans le relire serait annoncer une intention pour un fait.
    $relu = Get-Content $fichier -Raw -Encoding UTF8 | ConvertFrom-Json
    Note "Mode              $($relu.Agent.Mode)"
    Note "Stabilite         $($relu.Agent.StabiliteMinutes) min"
    Note "Fenetre           $($relu.Agent.FenetreJours) jours"
    Note "Envois par tour   $($relu.Agent.LimiteEnvoisParTour) au plus"
    Note "Journal           $($relu.Agent.CheminJournal)"
    Note "Registre          $($relu.Fne.CertificationLedgerPath)"

    # Affiches, parce qu'ils sont restes invisibles pendant que la DGI refusait
    # toutes les factures a cause d'eux.
    Note "Point de vente    $($relu.Fne.PointOfSale)"
    Note "Etablissement     $($relu.Fne.Establishment)"

    # Le miroir vers la base d'audit. Affiche meme eteint : une fonction dont on
    # ignore qu'elle est eteinte se croit en panne.
    $urlSaas = if ($relu.PSObject.Properties['Saas']) { $relu.Saas.Url } else { '' }
    $dossierSaas = if ($relu.PSObject.Properties['Saas']) { $relu.Saas.DossierId } else { '' }
    $cleSaas = [Environment]::GetEnvironmentVariable('Saas__CleService', 'Machine')

    Note ""
    if ($urlSaas -and $dossierSaas -and $cleSaas) {
        Note "Base d'audit      $urlSaas"
        Note "                  dossier $dossierSaas"
        Note "                  L'agent y reflete son registre a chaque tour."
        Note "                  Le registre local reste la seule reference."
    }
    else {
        $manque = @()
        if (-not $urlSaas)     { $manque += 'Saas:Url' }
        if (-not $dossierSaas) { $manque += 'Saas:DossierId' }
        if (-not $cleSaas)     { $manque += 'Saas__CleService (variable machine)' }
        Note "Base d'audit      eteinte - il manque $($manque -join ', ')."
        Note "                  La certification fonctionne exactement pareil."
    }

    $identiteAFaire = @()
    foreach ($paire in @(
        @{ Nom = 'Fne:PointOfSale';  Valeur = $relu.Fne.PointOfSale },
        @{ Nom = 'Fne:Establishment'; Valeur = $relu.Fne.Establishment })) {

        if ([string]::IsNullOrWhiteSpace($paire.Valeur) -or
            $paire.Valeur -match $MotifGabarit) {
            $identiteAFaire += $paire.Nom
        }
    }

    if ($identiteAFaire.Count -gt 0) {
        Alerte ""
        Alerte "ATTENTION : $($identiteAFaire -join ' et ') n'est pas renseigne."
        Alerte "  La DGI refusera toutes les factures - « Establishment is invalid »."
        Alerte "  Ces valeurs vous sont donnees par la DGI avec votre acces a la"
        Alerte "  plateforme ; elles ne viennent pas de Sage. Posez-les ainsi :"
        Alerte ""
        Alerte "    powershell -ExecutionPolicy Bypass -File .\deploiement\installer-agent.ps1 ``"
        Alerte "      -Preparer -PointDeVente 'VOTRE_POINT' -Etablissement 'VOTRE_ETAB'"
        Alerte ""
        Alerte "  L'agent refuse d'envoyer tant qu'elles manquent : aucune facture"
        Alerte "  ne partira pour se faire refuser."
    }

    # L'adresse du tableau de bord, en toutes lettres. Un ecran que personne ne
    # sait ou trouver ne sert a rien, et le journal est le seul endroit ou l'on
    # regarde quand quelque chose ne va pas.
    if ($relu.Agent.TableauActif -ne $false) {
        $portTableau = if ($relu.Agent.TableauPort) { $relu.Agent.TableauPort } else { 5080 }
        Note ""
        Note "Tableau de bord   http://localhost:$portTableau"
        Note "                  La liste des factures et le bouton « Certifier »."
        Note "                  Depuis ce poste uniquement : aucune machine du"
        Note "                  reseau ne peut l'atteindre."
    }
    Note ""
    Note "Lu dans $fichier. Ces reglages prennent effet au prochain demarrage"
    Note "du service, sans redemarrage de Windows."

    # Les variables machine qui doublaient ces reglages ne sont retirees
    # qu'ICI : le fichier est ecrit, relu, et porte bien les valeurs. Les
    # effacer avant aurait laisse - et a laisse une fois - le poste sans
    # reglage d'aucun cote quand l'ecriture echouait.
    #
    # Une seule source par reglage, sinon on ne sait plus laquelle s'applique.
    # Les secrets ne sont pas concernes : ils restent en variables machine, hors
    # de tout fichier.
    if ($relu.Agent.Mode -and $relu.Fne.CertificationLedgerPath -and $relu.Agent.CheminJournal) {
        foreach ($nom in @('Fne__CertificationLedgerPath', 'Agent__CheminJournal',
                           'Agent__FenetreJours', 'Agent__StabiliteMinutes', 'Agent__Mode')) {
            if ([Environment]::GetEnvironmentVariable($nom, 'Machine')) {
                [Environment]::SetEnvironmentVariable($nom, $null, 'Machine')
                Remove-Item "env:$nom" -ErrorAction SilentlyContinue
                Note "$nom : variable machine retiree, le fichier fait foi."
            }
        }
    }
    else {
        Alerte "Le fichier relu ne porte pas tous les reglages attendus : les variables"
        Alerte "machine sont conservees. Ne les effacez pas tant que ce n'est pas regle."
    }

    if ($relu.Agent.Mode -eq 'Automatic') {
        Alerte "Mode Automatic : les factures conformes et stables partiront"
        Alerte "d'elles-memes, sans confirmation. Retour en arriere :"
        Alerte "  -Preparer -Mode Manual"
    }

    # Le perimetre, lu apres la publication : ce bloc affiche la date que le
    # binaire chargera reellement, et non celle qu'il devrait charger. Annoncer
    # une date sans l'avoir lue serait l'affirmation sans preuve que ce projet
    # s'emploie a bannir.
    Titre 'Perimetre FNE'
    $demarrageMachine = [Environment]::GetEnvironmentVariable('Fne__DemarrageLe', 'Machine')

    if ($DemarrageLe) {
        if ($DemarrageLe -notmatch '^\d{4}-\d{2}-\d{2}$') {
            throw "-DemarrageLe attend une date au format AAAA-MM-JJ, pas « $DemarrageLe »."
        }

        [Environment]::SetEnvironmentVariable('Fne__DemarrageLe', $DemarrageLe, 'Machine')
        Set-Item 'env:Fne__DemarrageLe' $DemarrageLe
        Alerte "Derogation posee sur ce poste : demarrage au $DemarrageLe."
        Alerte "Elle prime sur appsettings.json. Pour revenir a la valeur versionnee,"
        Alerte "effacez la variable machine Fne__DemarrageLe."
    }
    elseif ($demarrageMachine) {
        Alerte "Une derogation machine existe deja : Fne__DemarrageLe = $demarrageMachine."
        Alerte "Elle prime sur appsettings.json. Effacez-la pour suivre la valeur versionnee."
    }
    else {
        # Lue dans le fichier plutot qu'annoncee : afficher une date que le
        # binaire n'appliquerait pas serait exactement le genre d'affirmation
        # sans preuve que ce projet s'emploie a bannir.
        $fichier = Join-Path $Destination 'appsettings.json'
        $lue = if (Test-Path $fichier) {
            (Get-Content $fichier -Raw -Encoding UTF8 | ConvertFrom-Json).Fne.DemarrageLe
        } else { $null }

        if ($lue) {
            Note "Demarrage au $lue, lu dans $fichier."
            Note "Aucune facture anterieure ne sera candidate. Elles restent lues et"
            Note "affichees, mais classees « hors perimetre » - jamais « bloquees »."
        }
        else {
            Alerte "Aucune date de demarrage : tout l'historique du dossier est dans le"
            Alerte "perimetre. Posez Fne:DemarrageLe dans appsettings.json, ou relancez"
            Alerte "avec -DemarrageLe AAAA-MM-JJ pour deroger sur ce poste."
        }
    }

    # Le service reprend en DERNIER, une fois les reglages ecrits dans le
    # fichier qu'il va lire. Le redemarrer juste apres la publication le faisait
    # partir sur l'appsettings fraichement republie - donc sur les valeurs
    # versionnees, Manual et sans chemin de registre.
    if ($tournait) {
        Titre 'Redemarrage'
        Redemarrer-Service | Out-Null
    }
    elseif (Get-Service $NomService -ErrorAction SilentlyContinue) {
        # Le service existe mais ne tournait pas. Le demarrer d'office serait
        # passer outre une decision d'exploitation ; se taire laisserait croire
        # que tout va bien alors que rien n'examine les factures.
        Titre 'Service'
        Alerte "Le service $NomService existe mais est ARRETE. Aucune facture n'est"
        Alerte "examinee ni envoyee. Les reglages ci-dessus l'attendent :"
        Alerte "  sc.exe start $NomService"
    }
}

# --- Vérification -----------------------------------------------------------

function Taille-Journal([string]$fichier) {
    if (Test-Path $fichier) { return (Get-Item $fichier).Length }
    return 0
}

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

    # Absent et vide comptent tous deux pour zéro. Avec -1 pour l'absence, un
    # fichier créé mais vide passait de -1 a 0 : le script y lisait une
    # croissance et déclarait la vérification passée sur un journal muet.
    $avant = Taille-Journal $journal

    # Start-Process -Wait : le binaire est compilé sans console, PowerShell ne
    # l'attendrait pas et l'on lirait un journal encore vide.
    $processus = Start-Process $exe -ArgumentList '--verifier' -Wait -PassThru -NoNewWindow

    $apres = Taille-Journal $journal

    # Lues avant d'être montrées : le script doit compter ce qu'il affiche,
    # sinon il annonce « le journal le montre » au-dessus de rien.
    $lignes = @()
    if (Test-Path $journal) {
        $lignes = @(Get-Content $journal -Encoding UTF8 | Where-Object { $_.Trim() -ne '' })
    }

    Titre 'Journal'
    Note "Fichier : $journal"
    Note "Taille : $avant octet(s) avant, $apres apres. Lignes : $($lignes.Count)."
    # Write-Host, pas le pipeline : l'appelant fait « Verifier-Poste | Out-Null »
    # pour jeter le $true de retour, et emportait avec lui les lignes du journal.
    # Le titre « Journal » s'affichait alors au-dessus de rien, suivi de
    # « le journal le montre » - la preuve annoncée mais jamais produite.
    if ($lignes.Count -gt 0) {
        Note ''
        foreach ($ligne in ($lignes | Select-Object -Last 25)) { Note $ligne }
    }

    # Un journal absent, vide ou inchangé n'est pas une vérification qui passe :
    # c'est une vérification dont on ne sait rien. Les confondre serait déclarer
    # bon ce qu'on n'a pas regardé - la faute que tout ce projet s'emploie a
    # éviter.
    if ($apres -le $avant -or $lignes.Count -eq 0) {
        throw "L'agent n'a écrit aucune ligne dans $journal. Sans journal, cette " +
              "vérification ne prouve rien - ne la prenez pas pour un succès. Vérifiez " +
              "que le dossier est accessible en écriture, puis relancez."
    }

    if ($processus.ExitCode -ne 0) {
        throw "La vérification a échoué (code $($processus.ExitCode)). Le journal ci-dessus dit " +
              "pourquoi. N'installez pas le service tant que ce n'est pas réglé."
    }

    Bien "Vérification passée, et les $($lignes.Count) ligne(s) ci-dessus le montrent."
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

    # Le mode reel, lu la ou l'agent le lira. Le script annoncait « en mode
    # Manual » en dur : il aurait dit « il n'enverra rien » a un operateur qui
    # venait de poser Automatic, et l'aurait dit pendant que les factures
    # partaient. C'est le pire mensonge que ce projet puisse produire.
    # Lu dans le fichier que le service chargera, non dans une variable machine :
    # les reglages non secrets n'y vivent plus, precisement parce qu'elles
    # n'atteignaient pas toujours le service.
    $fichierRegle = Join-Path $Destination 'appsettings.json'
    $modeReel = 'Manual'
    if (Test-Path $fichierRegle) {
        $lu = (Get-Content $fichierRegle -Raw -Encoding UTF8 | ConvertFrom-Json).Agent.Mode
        if ($lu) { $modeReel = $lu }
    }

    if ($modeReel -eq 'Automatic') {
        Alerte "MODE AUTOMATIC. Des le premier tour - une minute apres le demarrage - les"
        Alerte "factures conformes et stables partiront d'elles-memes vers la DGI, sans"
        Alerte "confirmation, au plus dix par tour."
        Alerte "Pour arreter : sc.exe stop $NomService"
        Note ""
    }

    if (Get-Service $NomService -ErrorAction SilentlyContinue) {
        Alerte "Le service $NomService existe déjà. Arrêt et suppression avant recréation."
        & sc.exe stop $NomService 2>&1 | Out-Null
        & sc.exe delete $NomService 2>&1 | Out-Null

        # Une suppression peut rester « pending » tant qu'un handle traine. La
        # creation echouerait alors avec un message obscur : mieux vaut attendre
        # que le service ait vraiment disparu, et le dire s'il s'attarde.
        $attendu = 0
        while ((Get-Service $NomService -ErrorAction SilentlyContinue) -and $attendu -lt 30) {
            Start-Sleep -Seconds 1
            $attendu++
        }

        if (Get-Service $NomService -ErrorAction SilentlyContinue) {
            throw "Le service $NomService existe toujours apres $attendu s. Fermez la console " +
                  "de gestion des services si elle est ouverte, puis relancez."
        }
    }

    & sc.exe create $NomService binPath= "`"$exe`"" start= auto DisplayName= "SageFne Agent" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "La création du service a échoué." }

    & sc.exe description $NomService "Certification FNE des factures Sage. Mode $modeReel." | Out-Null

    # Redémarrage automatique après incident, mais pas en boucle : un agent qui
    # redémarre toutes les secondes noie son journal et n'avance à rien.
    & sc.exe failure $NomService reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Null

    $journal = Join-Path $Journaux "agent-$(Get-Date -Format 'yyyy-MM-dd').log"
    $avant = Taille-Journal $journal

    & sc.exe start $NomService | Out-Null

    $service = Get-Service $NomService
    if ($service.Status -ne 'Running') {
        Start-Sleep -Seconds 3
        $service = Get-Service $NomService
    }

    if ($service.Status -ne 'Running') {
        throw "Le service ne tourne pas (état : $($service.Status)). Lisez $Journaux."
    }

    # « Running » ne prouve que l'existence d'un processus. Il ne dit ni que la
    # configuration est arrivee, ni sur quelles donnees l'agent travaille, ni
    # meme qu'il sait ecrire. C'est exactement le feu vert sans preuve que ce
    # projet a deja produit trois fois. On attend donc le journal.
    Titre 'Premier tour'
    Note "Le service tourne. Reste a savoir ce qu'il fait : on attend son journal."

    $attendu = 0
    while ((Taille-Journal $journal) -le $avant -and $attendu -lt 90) {
        Start-Sleep -Seconds 2
        $attendu += 2
    }

    $lignes = @()
    if (Test-Path $journal) {
        $lignes = @(Get-Content $journal -Encoding UTF8 | Where-Object { $_.Trim() -ne '' })
    }

    if ((Taille-Journal $journal) -le $avant) {
        throw "Le service tourne mais n'a rien ecrit dans $journal en $attendu s. On ne sait " +
              "donc pas ce qu'il fait, et « demarre » ne vaut pas « fonctionne ». Arretez-le " +
              "(sc.exe stop $NomService), verifiez que le dossier est accessible en ecriture, " +
              "et relisez l'Observateur d'evenements."
    }

    Note ""
    foreach ($ligne in ($lignes | Select-Object -Last 25)) { Note $ligne }
    Note ""

    $recentes = $lignes | Select-Object -Last 25

    # Un service ne demarre pas sous le compte qui l'installe, et le
    # gestionnaire de services garde en cache l'environnement machine tel qu'il
    # etait a l'amorcage. Une variable posee il y a cinq minutes peut donc lui
    # rester invisible : l'agent retombe alors sur le jeu d'essai, tourne
    # parfaitement, et ne certifie rien de reel. Le journal est le seul endroit
    # ou cela se voit.
    if ($recentes -match "JEU D'ESSAI") {
        throw "Le service tourne mais lit le JEU D'ESSAI, pas votre dossier Sage. Les " +
              "variables machine posees par -Preparer ne lui sont pas parvenues : le " +
              "gestionnaire de services garde en cache l'environnement tel qu'il etait au " +
              "demarrage de Windows. Redemarrez le poste, puis relancez -Installer. Rien " +
              "n'a ete certifie : le jeu d'essai ne parle a aucune plateforme."
    }

    # Le mode qui compte est celui que le service annonce, pas celui que porte
    # la variable. Les deux peuvent differer - c'est meme le symptome d'une
    # variable machine invisible au service - et affirmer la variable pendant
    # que le journal dit le contraire serait poser deux verites pour un fait.
    $modeJournal = $null
    if ($recentes -match 'Mode AUTOMATIC')          { $modeJournal = 'Automatic' }
    elseif ($recentes -match 'Mode Manual')         { $modeJournal = 'Manual' }
    elseif ($recentes -match 'Mode SemiAutomatic')  { $modeJournal = 'SemiAutomatic' }

    if (-not $modeJournal) {
        Alerte "Le journal n'annonce aucun mode. La variable machine porte « $modeReel », mais"
        Alerte "rien ne prouve que le service la voit. Lisez les lignes ci-dessus avant de"
        Alerte "compter sur son comportement."
        Bien "Service $NomService demarre, et le journal le montre."
    }
    elseif ($modeJournal -ne $modeReel) {
        Alerte "DESACCORD. La variable machine porte « $modeReel », le service annonce"
        Alerte "« $modeJournal ». C'est le service qui fait foi : c'est en $modeJournal qu'il"
        Alerte "tourne. La cause la plus frequente est une variable posee apres l'amorçage de"
        Alerte "Windows, que le gestionnaire de services ne voit pas. Redemarrez le poste,"
        Alerte "puis relancez -Installer."
        Bien "Service $NomService demarre, et il tourne en $modeJournal."
    }
    else {
        Bien "Service $NomService demarre en mode $modeJournal, et le journal le montre."
    }

    if ($modeJournal -eq 'Automatic') {
        Note "Les factures conformes et stables partent d'elles-memes, au plus dix par tour."
    }
    elseif ($modeJournal) {
        Note "Il observe et journalise. Il n'enverra rien tant qu'il tourne en $modeJournal."
    }

    Note ""
    Note "Pour le suivre :"
    Note "  Get-Content '$journal' -Wait -Encoding UTF8"
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
