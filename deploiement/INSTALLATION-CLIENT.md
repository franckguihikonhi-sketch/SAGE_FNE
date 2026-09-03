# Installer le middleware chez un client

Un fichier, `SageFneSetup.exe`. Rien à installer d'autre : ni .NET, ni Git, ni
SDK, ni PowerShell débridé.

## Où le prendre

Il est construit par l'intégration continue à chaque poussée, et attaché au
travail « Installeur Windows » sous le nom `SageFneSetup`. Prenez celui du
commit que vous voulez déployer — c'est ce qui permet de dire, six mois plus
tard, quelle version tourne chez qui.

Pour le construire vous-même, sur une machine avec le SDK .NET 8 :

```powershell
powershell -ExecutionPolicy Bypass -File .\deploiement\construire-installeur.ps1
```

## Ce qu'il faut avoir sous la main

| | Où le trouver |
|---|---|
| La chaîne de connexion Sage | **compte SQL en lecture seule**, créé pour ce middleware |
| La clé d'API FNE | délivrée par la DGI au contribuable, avec son NCC |
| Le point de vente | déclaré à la DGI, figure sur chaque facture |
| L'établissement | idem |

Facultatif, pour l'écran distant : l'adresse du projet Supabase, l'identifiant
du dossier, et la clé de service.

## Sur le poste du client

Clic droit sur `SageFneSetup.exe`, **Exécuter en tant qu'administrateur**. Il
pose un service Windows et des variables machine : sans ces droits il s'arrête
en le disant, avant d'avoir rien touché.

Il pose ensuite ses questions. La clé d'API et la clé de service ne s'affichent
pas pendant la saisie.

Pour un déploiement scripté, tout se passe en arguments — une commande par
ligne, `&&` n'existe pas dans ce shell :

```powershell
.\SageFneSetup.exe --silencieux --sage "Server=SRV;Database=BIJOU;User Id=lecteur_fne;Pwd=MOT_DE_PASSE" --cle-fne "VOTRE_CLE_DGI" --point-de-vente "FISH-AFRIC" --etablissement "FISH-AFRIC"
```

Ajoutez `--simulation` pour voir ce qui serait fait sans rien écrire.

## Ce qu'il refuse, et pourquoi

Toutes les vérifications ont lieu **avant la première écriture**. Une
installation qui s'arrête au milieu laisse une machine dans un état que
personne n'a voulu, et c'est arrivé.

| Refus | La raison |
|---|---|
| chaîne Sage absente ou au gabarit | la lecture retomberait sur le jeu d'essai, et une facture inventée est déjà réellement partie à la DGI pour cette raison |
| point de vente ou établissement manquant | aucun contrôle de pièce ne peut les voir : la facture partirait irréprochable et la DGI répondrait « Establishment is invalid » |
| registre dans un profil utilisateur | le service tourne sous un autre compte et y écrirait un second registre ; deux registres ont déjà fait certifier deux fois la même facture |
| écran distant à moitié configuré | les trois valeurs vont ensemble, sinon le miroir reste éteint sans que personne le sache |

## Après l'installation

Le service démarre en mode **Manual** : il observe, journalise, et **n'envoie
rien** tant qu'un humain n'a pas cliqué. Ouvrez `http://localhost:5080` depuis
ce poste et regardez la liste avant toute autre chose.

Passer en `Automatic` est une décision d'exploitation, prise après avoir vu ce
que le middleware propose de certifier.

## Une réinstallation ne perd rien

Relancer l'exécutable sur un poste déjà installé conserve les réglages qui s'y
trouvent — mode, fenêtre, identité du dossier, section du SaaS. Un réglage
qu'on cesse de porter est un réglage perdu : la fenêtre est déjà retombée de
30 jours à 7 de cette façon, et l'identité du dossier à « A_COMPLETER », ce qui
a fait refuser toutes les factures sans un mot.

## À dire au client, une fois

**Le registre `C:\ProgramData\SageFne\certifications.json` est la seule mémoire
des certifications.** Sage n'en porte aucune trace. Le perdre ferait repartir à
la DGI des factures déjà certifiées, et une facture certifiée deux fois ne se
reprend que par un avoir. Il doit entrer dans le plan de sauvegarde.

## Un agent par dossier Sage, jamais deux

Deux agents sur la même base tiennent deux registres qui s'ignorent : chacun
lit les mêmes factures, chacun croit qu'elles ne sont pas parties, et chacun
les envoie.

Les autres postes du client n'ont **pas besoin d'agent**. Ils ouvrent l'écran
distant dans un navigateur. Dix comptables, un seul agent.

Rien n'empêche encore techniquement d'en installer deux : c'est une consigne,
pas une garantie, et c'est le prochain chantier.
