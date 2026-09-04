# Chez le client, avant d'installer

`AVANT-INSTALLATION.md` dit ce qu'il faut avoir préparé. Cette fiche-ci dit ce
qu'on fait **sur le poste, devant le client**, dans l'ordre, avant d'écrire quoi
que ce soit.

---

## Le seul danger qui compte vraiment

Ce n'est pas technique. **Chaque client a son propre accès FNE** : sa clé, son
NCC, son point de vente. Rien ne se partage entre X, Y et Z.

Installer la clé de Y sur le poste de X ferait certifier les factures de X
**sous le NCC de Y**. Ce n'est pas une erreur informatique, c'est une fausse
déclaration fiscale — et elle ne se reprend que par un avoir, facture par
facture.

Tout ce qui suit existe pour rendre cette confusion impossible.

---

## 1. Ouvrir la fiche de CE client

Sortez la fiche remplie dans `AVANT-INSTALLATION.md`. Une colonne par client.
**Ne travaillez jamais de mémoire entre deux clients dans la même journée.**

Relisez à voix haute, avec le client :

- son NCC ;
- son point de vente et son établissement, **orthographe comprise** ;
- l'environnement visé : essai, toujours, pour une première installation.

---

## 2. Éprouver, sans rien écrire

Une seule commande. Elle **n'écrit rien** : ni service, ni fichier, ni variable.

```powershell
.\SageFneSetup.exe --verifier --sage "VOTRE_CHAINE_SAGE" --point-de-vente "SON_POINT" --etablissement "SON_ETABLISSEMENT"
```

Elle répond en quatre parties.

### « Ce poste »

Si une identité y est déjà posée, elle s'affiche. Deux cas :

- **La même que celle que vous installez** — c'est une réinstallation, rien à
  signaler.
- **Une autre** — un avertissement sort. Arrêtez-vous et comprenez pourquoi :
  soit vous n'êtes pas sur le bon poste, soit ce poste change de client. Dans ce
  second cas, **mettez l'ancien registre de côté sans l'effacer** : il porte les
  certifications de l'ancien client, et c'est la seule trace de ce qui a été
  déclaré à la DGI pour lui.

### « Ce qui serait posé »

Le point de vente, l'établissement, l'environnement, et la clé masquée.
**Faites confirmer ces trois valeurs par le client, à voix haute.**

### « La base Sage »

La liste des documents réellement trouvés : domaine, type, nombre, et un
exemplaire avec son compte tiers.

> **Faites reconnaître ces documents au client.** S'il ne reconnaît ni les
> numéros de pièce ni les comptes tiers, la chaîne de connexion ne désigne pas
> son dossier — et rien ne doit être installé.

C'est le contrôle le plus important de toute la procédure. Une chaîne de
connexion recopiée d'un client à l'autre passerait tous les autres tests.

### « Rien n'a été écrit »

Confirme que le poste est intact.

---

## 3. Le réseau, depuis ce poste

```powershell
Test-NetConnection 54.247.95.108 -Port 80
```

Si ça ne répond pas, l'installation réussira et **rien ne partira jamais**.
Faites ouvrir le flux avant de continuer, ou notez-le comme point bloquant.

---

## 4. Installer

Seulement une fois les trois étapes précédentes passées.

```powershell
.\SageFneSetup.exe
```

Clic droit, **Exécuter en tant qu'administrateur**. Il pose ses questions ; la
clé d'API ne s'affiche pas pendant la saisie.

Si une identité différente est en place, il le redira ici aussi, avant d'écrire.

---

## 5. Vérifier que ça vit

```powershell
Get-Service SageFneAgent
Start-Process http://localhost:5080
```

Le tableau de bord doit lister les factures du dossier. Le service démarre en
mode **Manual** : il observe et n'envoie rien.

**Regardez la liste avec le client.** Les pièces bloquées y disent pourquoi en
clair — NCC manquant, TVA absente, désignation vide. C'est le moment de lui
montrer ce qu'il aura à corriger dans Sage, pendant que vous êtes là.

---

## 6. Une seule facture, en essai

Ne partez pas sans avoir certifié une facture réelle, devant le client.

Choisissez-en une simple, choisissez son mode de règlement, cliquez. La réponse
de la DGI s'affiche mot pour mot. Vérifiez ensuite sur le portail que la
référence y figure.

C'est cette minute-là qui prouve la chaîne entière : Sage, les contrôles, la
clé, le réseau, la DGI. Tout le reste n'est que du paramétrage.

---

## 7. Avant de partir

- [ ] Le registre `C:\ProgramData\SageFne\certifications.json` est entré dans
      le plan de sauvegarde du client, et quelqu'un l'a acté.
- [ ] Le client sait que le mode reste **Manual** tant qu'il ne demande pas
      autre chose.
- [ ] La liste des corrections à faire dans Sage lui a été remise.
- [ ] La fiche du client est complétée et rangée — c'est elle qui vous dira,
      dans six mois, ce qui a été posé sur ce poste.

---

## Si vous enchaînez plusieurs clients dans la journée

Le risque de confusion est à son maximum. Trois règles simples :

1. **Une fiche ouverte à la fois.** Fermez celle du client précédent.
2. **`--verifier` sur chaque poste**, même si vous êtes sûr. Il coûte trente
   secondes et il lit la base, ce que votre mémoire ne fait pas.
3. **Ne recopiez jamais une chaîne de connexion d'un client à l'autre.** C'est
   l'erreur qui passe tous les contrôles sauf celui-là : le poste installé
   lirait le dossier du client précédent et certifierait ses factures sous le
   NCC du nouveau.
