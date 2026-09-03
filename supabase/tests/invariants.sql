\set ON_ERROR_STOP on
\pset pager off

create or replace function attendre_echec(sql text, attendu text) returns text
language plpgsql as $$
begin
  execute sql;
  return format('ÉCHEC — « %s » aurait dû être refusé', attendu);
exception when others then
  return format('OK     refusé : %s', attendu);
end;
$$;

-- Un jeu minimal
insert into tenants (id, nom) values ('11111111-1111-1111-1111-111111111111', 'SITA SARL');
insert into dossiers (id, tenant_id, code, base_sage)
values ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'HT', 'HT');

insert into certifications (id, dossier_id, identite, piece, do_type, do_doc_type, etat, total_ttc)
values ('33333333-3333-3333-3333-333333333333',
        '22222222-2222-2222-2222-222222222222', '0/6/1052', '1052', 6, 6, 'ready', 120000);

\echo '--- Anti-doublon ---'
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1052', '1052')$$,
  'deux certifications pour la même identité');

\echo '--- La comptabilisation ne crée pas de doublon ---'
-- Même pièce passée de DO_Type 6 à 7 : l'identité ne change pas, donc conflit.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, do_type, do_doc_type)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1052', '1052', 7, 6)$$,
  'la même facture réinsérée après comptabilisation');

\echo '--- Machine à états ---'
update certifications set etat = 'sending' where id = '33333333-3333-3333-3333-333333333333';
select case when envoyee_le is not null then 'OK     envoyee_le posée automatiquement'
            else 'ÉCHEC — envoyee_le est restée nulle' end
  from certifications where id = '33333333-3333-3333-3333-333333333333';

select attendre_echec($$
  update certifications set etat = 'ready' where id = '33333333-3333-3333-3333-333333333333'$$,
  'sending qui redevient ready en silence');

update certifications set etat = 'certified', reference_fne = '2304903U26000001052'
 where id = '33333333-3333-3333-3333-333333333333';
select case when certifiee_le is not null then 'OK     certifiee_le posée automatiquement'
            else 'ÉCHEC — certifiee_le est restée nulle' end
  from certifications where id = '33333333-3333-3333-3333-333333333333';

select attendre_echec($$
  update certifications set etat = 'ready' where id = '33333333-3333-3333-3333-333333333333'$$,
  'une pièce certifiée qui redeviendrait à envoyer');

select attendre_echec($$
  update certifications set reference_fne = 'AUTRE' where id = '33333333-3333-3333-3333-333333333333'$$,
  'la référence d''une pièce certifiée, modifiée');

\echo '--- Certifiée sans référence ---'
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat)
  values ('22222222-2222-2222-2222-222222222222', '0/6/9999', '9999', 'certified')$$,
  'certifiée sans référence FNE');

\echo '--- Trace en ajout seul ---'
select case when count(*) = 3 then format('OK     %s événements tracés automatiquement', count(*))
            else format('ÉCHEC — %s événements au lieu de 3', count(*)) end
  from certification_evenements where certification_id = '33333333-3333-3333-3333-333333333333';

select attendre_echec($$
  update certification_evenements set message = 'réécrit' where certification_id is not null$$,
  'la réécriture de l''historique');
select attendre_echec($$delete from certification_evenements where certification_id is not null$$,
  'la suppression de l''historique');

\echo '--- Sortie nommée de sending ---'
insert into certifications (id, dossier_id, identite, piece, etat)
values ('44444444-4444-4444-4444-444444444444',
        '22222222-2222-2222-2222-222222222222', '0/6/1053', '1053', 'sending');

select attendre_echec($$select debloquer_envoi('44444444-4444-4444-4444-444444444444', '')$$,
  'un déblocage sans motif');
select attendre_echec($$
  select debloquer_envoi('44444444-4444-4444-4444-444444444444', 'vérifié', true, '')$$,
  'un déblocage « certifiée » sans référence');

select case when (debloquer_envoi('44444444-4444-4444-4444-444444444444',
                    'Portail DGI : aucune trace de cette facture le 31/08.')).etat = 'error'
            then 'OK     déblocage motivé accepté'
            else 'ÉCHEC — état inattendu après déblocage' end;

select case when message like 'Portail DGI%' then 'OK     le motif est tracé'
            else format('ÉCHEC — motif absent de la trace : %s', message) end
  from certification_evenements
 where certification_id = '44444444-4444-4444-4444-444444444444'
 order by id desc limit 1;

\echo '--- Règles de TVA 0 % ---'
-- Une règle naît en brouillon, et un brouillon ne produit rien.
insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle)
values ('22222222-2222-2222-2222-222222222222', 'article-13415001', 1, 'article', '13415001');

select case when not regle_applicable(r.etat, r.code, r.valide_du, r.valide_au, now()) then 'OK     un brouillon ne produit aucun code'
            else 'ÉCHEC — un brouillon a été jugé applicable' end
  from regles_tva_zero r
 where r.regle_id = 'article-13415001' and r.version = 1;

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle)
  values ('22222222-2222-2222-2222-222222222222', 'article-sans-cle', 1, 'article', '')$$,
  'une règle d''article sans clé');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle)
  values ('22222222-2222-2222-2222-222222222222', 'dossier-avec-cle', 1, 'dossier', '13415001')$$,
  'une règle de dossier avec une clé');

-- Un régime se déclare compte par compte : la règle porte l'acheteur ET son
-- régime. Il manque l'un ou l'autre, elle est refusée.
select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, regime)
  values ('22222222-2222-2222-2222-222222222222', 'regime-sans-regime', 1,
          'regime_acheteur', 'CT001', '')$$,
  'une règle de régime acheteur sans régime');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, regime)
  values ('22222222-2222-2222-2222-222222222222', 'regime-sans-client', 1,
          'regime_acheteur', '', 'TEE')$$,
  'une règle de régime acheteur sans compte client');

\echo '--- Une règle validée porte sa preuve ---'
select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, code, fondement, etat)
  values ('22222222-2222-2222-2222-222222222222', 'article-sans-preuve', 1,
          'article', '25SN001', 'tvac', 'convention', 'validee')$$,
  'une validation sans validateur ni référence');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, code, etat,
                               validee_par, validee_le, reference)
  values ('22222222-2222-2222-2222-222222222222', 'article-sans-fondement', 1,
          'article', '25SN001', 'tvac', 'validee', 'Franck', now(), 'Convention 42')$$,
  'une validation sans fondement juridique');

-- Avec le code, le fondement, le validateur et la preuve, elle passe.
insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, code, fondement, etat,
                             validee_par, validee_le, reference)
values ('22222222-2222-2222-2222-222222222222', 'article-25SN001', 1,
        'article', '25SN001', 'tvac', 'convention', 'validee',
        'Franck', now(), 'Convention DGI n° 42 du 12/03/2026');

select case when regle_applicable(r.etat, r.code, r.valide_du, r.valide_au, now()) then 'OK     une règle validée sur preuve produit son code'
            else 'ÉCHEC — la règle validée a été jugée inapplicable' end
  from regles_tva_zero r
 where r.regle_id = 'article-25SN001' and r.version = 1;

\echo '--- Une version qui a servi ne se réécrit pas ---'
select attendre_echec($$
  update regles_tva_zero set code = 'tvad'
   where regle_id = 'article-25SN001' and version = 1$$,
  'la modification d''une version existante');

select attendre_echec($$
  delete from regles_tva_zero where regle_id = 'article-25SN001'$$,
  'la suppression d''une règle');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle)
  values ('22222222-2222-2222-2222-222222222222', 'article-25SN001', 1, 'article', '25SN001')$$,
  'une version 1 rejouée');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle)
  values ('22222222-2222-2222-2222-222222222222', 'article-25SN001', 4, 'article', '25SN001')$$,
  'une version intercalée hors suite');

\echo '--- Révoquer, c''est ajouter une version ---'
insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, code, fondement,
                             etat, note)
values ('22222222-2222-2222-2222-222222222222', 'article-25SN001', 2,
        'article', '25SN001', 'tvac', 'convention', 'revoquee',
        'Convention échue au 31/12/2026, non renouvelée.');

select case when count(*) = 2 then 'OK     la version révoquée s''ajoute, la précédente reste'
            else format('ÉCHEC — %s version(s) au registre', count(*)) end
  from regles_tva_zero where regle_id = 'article-25SN001';

select case when version = 2 and etat = 'revoquee'
            then 'OK     la vue courante montre la dernière version'
            else format('ÉCHEC — version courante %s en état %s', version, etat) end
  from regles_tva_zero_courantes where regle_id = 'article-25SN001';

select case when not regle_applicable(r.etat, r.code, r.valide_du, r.valide_au, now())
            then 'OK     une règle révoquée ne produit plus son code'
            else 'ÉCHEC — la règle révoquée reste applicable' end
  from regles_tva_zero_courantes r where r.regle_id = 'article-25SN001';

\echo '--- Les bornes de validité valent ---'
select attendre_echec($$
  insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, valide_du, valide_au)
  values ('22222222-2222-2222-2222-222222222222', 'bornes-inversees', 1, 'article', 'X',
          now(), now() - interval '1 day')$$,
  'des bornes de validité inversées');

insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, regime, code,
                             fondement, etat, validee_par, validee_le, reference, valide_au)
values ('22222222-2222-2222-2222-222222222222', 'regimeacheteur-ct001', 1,
        'regime_acheteur', 'CT001', 'TEE', 'tvad', 'regime_acheteur', 'validee',
        'Franck', now(), 'Attestation TEE du 05/01/2026', now() - interval '1 day');

select case when not regle_applicable(r.etat, r.code, r.valide_du, r.valide_au, now())
            then 'OK     une règle expirée cesse de produire son code'
            else 'ÉCHEC — la règle expirée reste applicable' end
  from regles_tva_zero r where r.regle_id = 'regimeacheteur-ct001';

\echo '--- Les règles douteuses se signalent sans se corriger ---'
insert into regles_tva_zero (dossier_id, regle_id, version, portee, cle, regime, code,
                             fondement, etat, validee_par, validee_le, reference)
values ('22222222-2222-2222-2222-222222222222', 'regimeacheteur-ct002', 1,
        'regime_acheteur', 'CT002', 'RME', 'tvac', 'regime_acheteur', 'validee',
        'Franck', now(), 'Attestation RME');

select case when count(*) = 1 then 'OK     un fondement de régime sans TVAD est signalé'
            else format('ÉCHEC — %s signalement(s)', count(*)) end
  from regles_tva_zero_a_relire
 where regle_id = 'regimeacheteur-ct002'
   and observation like '%TVAD%';

-- Signalée, pas refusée : la vue expose, elle ne tranche pas.
select case when code = 'tvac' then 'OK     la règle signalée reste telle qu''elle a été écrite'
            else 'ÉCHEC — la règle a été corrigée d''office' end
  from regles_tva_zero_courantes where regle_id = 'regimeacheteur-ct002';

\echo '--- Sous quelle version une facture est partie ---'
insert into certification_regles_appliquees (certification_id, regle, ligne, article)
select '33333333-3333-3333-3333-333333333333', r.id, 1, '25SN001'
  from regles_tva_zero r where r.regle_id = 'article-25SN001' and r.version = 1;

-- La règle a été révoquée depuis. Le lien pointe toujours la version 1.
select case when version = 1 and etat = 'validee'
            then 'OK     la facture garde la version sous laquelle elle est partie'
            else format('ÉCHEC — version %s en état %s', version, etat) end
  from certification_regles_appliquees a
  join regles_tva_zero r on r.id = a.regle
 where a.certification_id = '33333333-3333-3333-3333-333333333333';

select attendre_echec($$
  delete from certification_regles_appliquees
   where certification_id = '33333333-3333-3333-3333-333333333333'$$,
  'la suppression du lien entre une facture et sa règle');

\echo '--- Transmise : arrivée au portail, en attente de clic ---'
insert into certifications (id, dossier_id, identite, piece, etat)
values ('66666666-6666-6666-6666-666666666666',
        '22222222-2222-2222-2222-222222222222', '0/6/1221', '1221', 'sending');

update certifications set etat = 'transmise'
 where id = '66666666-6666-6666-6666-666666666666';

select case when count(*) = 1 then 'OK     une pièce en suspens peut être constatée au portail'
            else 'ÉCHEC — le passage en transmise a été refusé' end
  from certifications
 where id = '66666666-6666-6666-6666-666666666666' and etat = 'transmise';

-- Le mur qui compte : renvoyable, elle ferait un doublon au portail.
select attendre_echec($$
  update certifications set etat = 'ready'
   where id = '66666666-6666-6666-6666-666666666666'$$,
  'le retour d''une pièce transmise en « prête »');

select attendre_echec($$
  update certifications set etat = 'pending'
   where id = '66666666-6666-6666-6666-666666666666'$$,
  'le retour d''une pièce transmise en « en attente »');

select case when count(*) = 1 then 'OK     la vue liste les pièces au portail'
            else format('ÉCHEC — %s ligne(s) dans la vue', count(*)) end
  from certifications_au_portail;

-- Le clic passé, elle se certifie.
update certifications set etat = 'certified', reference_fne = 'FNE-2026-1221'
 where id = '66666666-6666-6666-6666-666666666666';

select case when count(*) = 1 then 'OK     le clic passé, elle devient certifiée'
            else 'ÉCHEC — la certification a été refusée' end
  from certifications
 where id = '66666666-6666-6666-6666-666666666666' and etat = 'certified';

select case when count(*) = 0 then 'OK     la vue se vide quand le clic est passé'
            else 'ÉCHEC — la pièce certifiée reste listée au portail' end
  from certifications_au_portail;

\echo '--- RLS ---'
-- Aucune table sans RLS, plutôt qu'un compte figé : une table ajoutée sans
-- politique doit faire échouer ce test, pas seulement décaler un nombre.
select case when count(*) = 0 then 'OK     aucune table sans RLS'
            else format('ÉCHEC — %s table(s) sans RLS : %s',
                        count(*), string_agg(tablename, ', ')) end
  from pg_tables where schemaname = 'public' and not rowsecurity;

\echo '--- Vue des envois en suspens ---'
-- 1053 vient d'être débloquée : la vue doit être vide, c'est tout son intérêt.
select case when count(*) = 0 then 'OK     la vue se vide quand l''envoi est tranché'
            else format('ÉCHEC — %s ligne(s) après déblocage', count(*)) end
  from certifications_en_suspens;

insert into certifications (dossier_id, identite, piece, etat)
values ('22222222-2222-2222-2222-222222222222', '0/6/1054', '1054', 'sending');

select case when count(*) = 1 and max(piece) = '1054'
            then 'OK     la vue remonte le nouvel envoi en suspens'
            else format('ÉCHEC — %s ligne(s) dans la vue', count(*)) end
  from certifications_en_suspens;

select case when depuis is not null then 'OK     la vue dit depuis quand il attend'
            else 'ÉCHEC — depuis est nulle' end
  from certifications_en_suspens limit 1;

\echo '--- Environnement figé sur la certification ---'
-- Les lignes existantes ont hérité de l'environnement de leur dossier.
select case when environnement = 'test' then 'OK     l''environnement est repris du dossier'
            else format('ÉCHEC — environnement %s', environnement) end
  from certifications where identite = '0/6/1054';

select attendre_echec($$
  update certifications set environnement = 'production' where identite = '0/6/1054'$$,
  'le passage d''une certification de test en production');

\echo '--- Unicité par environnement ---'
-- La même identité dans le même dossier et le même environnement : refusée.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1054', '1054', 'pending', 'test')$$,
  'un doublon dans le même environnement');

-- La même identité en production : acceptée, ce sont deux plateformes.
insert into certifications (dossier_id, identite, piece, etat, environnement, reference_fne)
values ('22222222-2222-2222-2222-222222222222', '0/6/1054', '1054', 'certified', 'production', 'REF-PROD');

select case when count(*) = 2 then 'OK     test et production coexistent pour une même pièce'
            else format('ÉCHEC — %s ligne(s) pour 0/6/1054', count(*)) end
  from certifications where identite = '0/6/1054';

\echo '--- Le compteur de tentatives ---'
update certifications set tentatives = 2 where identite = '0/6/1054' and environnement = 'test';

select attendre_echec($$
  update certifications set tentatives = 1
   where identite = '0/6/1054' and environnement = 'test'$$,
  'un compteur de tentatives qui recule');

select case when tentatives = 2 then 'OK     le compteur de tentatives se conserve'
            else format('ÉCHEC — tentatives = %s', tentatives) end
  from certifications where identite = '0/6/1054' and environnement = 'test';

\echo '--- Réconciliation manuelle ---'
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement,
                              reference_fne, source)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1055', '1055', 'certified',
          'test', 'REF-1055', 'reconciliation_manuelle')$$,
  'une réconciliation sans date');

insert into certifications (dossier_id, identite, piece, etat, environnement,
                            reference_fne, source, reconciliee_le)
values ('22222222-2222-2222-2222-222222222222', '0/6/1055', '1055', 'certified',
        'test', 'REF-1055', 'reconciliation_manuelle', now());

select case when source = 'reconciliation_manuelle' and reconciliee_le is not null
            then 'OK     une réconciliation datée est acceptée'
            else 'ÉCHEC — la réconciliation n''a pas été inscrite' end
  from certifications where identite = '0/6/1055';

-- Une réconciliation ne peut pas conclure autre chose qu'une certification.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement,
                              source, reconciliee_le)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1056', '1056', 'error',
          'test', 'reconciliation_manuelle', now())$$,
  'une réconciliation qui ne certifie pas');

\echo '--- Le code HTTP retenu ---'
select attendre_echec($$
  update certifications set dernier_code_http = 42
   where identite = '0/6/1055'$$,
  'un code HTTP hors plage');

update certifications set dernier_code_http = 500 where identite = '0/6/1055';
select case when dernier_code_http = 500 then 'OK     le dernier code HTTP est conservé'
            else 'ÉCHEC — code HTTP non retenu' end
  from certifications where identite = '0/6/1055';

\echo '--- Certifiée sans référence ---'
-- La plateforme d'essai certifie sans toujours publier de référence. L'exiger
-- poussait à en inventer une, et c'est arrivé.
insert into certifications (dossier_id, identite, piece, etat, environnement,
                            source, reconciliee_le, motif)
values ('22222222-2222-2222-2222-222222222222', '0/6/1057', '1057', 'certified',
        'test', 'reconciliation_manuelle', now(),
        'Aucune référence FNE visible sur le portail/PDF TEST');

select case when reference_fne is null and token is null and etat = 'certified'
            then 'OK     certifiée sans référence ni jeton'
            else 'ÉCHEC — la certification sans référence a été refusée ou altérée' end
  from certifications where identite = '0/6/1057';

-- Mais pas sans rien : une certification que rien ne fonde reste refusée.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement,
                              source, reconciliee_le)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1058', '1058', 'certified',
          'test', 'reconciliation_manuelle', now())$$,
  'une certification manuelle sans référence ni motif');

-- Une certification du middleware sans référence signifierait qu'on a mal lu
-- la réponse de la DGI : elle reste interdite.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement,
                              source, motif)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1059', '1059', 'certified',
          'test', 'middleware', 'peu importe')$$,
  'une certification du middleware sans référence');

\echo '--- La chaîne vide ne remplace pas NULL ---'
select attendre_echec($$
  update certifications set reference_fne = '   ' where identite = '0/6/1057'$$,
  'une référence réduite à des espaces');

\echo '--- L''unicité ne dépend pas de la référence ---'
-- Le point qui protège du doublon : c'est l'identité Sage qui fait foi, jamais
-- le numéro de la DGI. Une pièce certifiée sans référence bloque autant.
select attendre_echec($$
  insert into certifications (dossier_id, identite, piece, etat, environnement,
                              source, reconciliee_le, motif)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1057', '1057', 'certified',
          'test', 'reconciliation_manuelle', now(), 'seconde tentative')$$,
  'un doublon d''une pièce certifiée sans référence');

select case when count(*) = 1 then 'OK     la vue liste les certifications sans référence'
            else format('ÉCHEC — %s ligne(s) dans la vue', count(*)) end
  from certifications_sans_reference;

\echo '--- Retirer une fausse référence sans défaire la certification ---'
insert into certifications (dossier_id, identite, piece, etat, environnement,
                            source, reconciliee_le, reference_fne)
values ('22222222-2222-2222-2222-222222222222', '0/6/1060', '1060', 'certified',
        'test', 'reconciliation_manuelle', now(), 'TA_REFERENCE_FNE');

update certifications
   set reference_fne = null,
       motif = 'Correction : référence « TA_REFERENCE_FNE » retirée, aucune référence au portail.'
 where identite = '0/6/1060';

select case when etat = 'certified' and reference_fne is null
                 and motif like 'Correction%'
            then 'OK     la référence part, la certification reste'
            else 'ÉCHEC — la correction a altéré la certification' end
  from certifications where identite = '0/6/1060';

\echo '--- Une référence ne se substitue jamais à une autre ---'
insert into certifications (dossier_id, identite, piece, etat, environnement,
                            source, reconciliee_le, reference_fne)
values ('22222222-2222-2222-2222-222222222222', '0/6/1061', '1061', 'certified',
        'test', 'reconciliation_manuelle', now(), 'REF-A');

select attendre_echec($$
  update certifications set reference_fne = 'REF-B', motif = 'peu importe'
   where identite = '0/6/1061'$$,
  'le remplacement d''une référence par une autre');

select attendre_echec($$
  update certifications set reference_fne = null where identite = '0/6/1061'$$,
  'un retrait de référence sans motif');

\echo '--- Une référence venue de la DGI ne se retire pas ---'
insert into certifications (dossier_id, identite, piece, etat, environnement,
                            source, reference_fne)
values ('22222222-2222-2222-2222-222222222222', '0/6/1062', '1062', 'certified',
        'test', 'middleware', 'REF-DGI');

select attendre_echec($$
  update certifications set reference_fne = null, motif = 'tentative'
   where identite = '0/6/1062'$$,
  'le retrait d''une référence lue dans la réponse de la DGI');

\echo '--- La correction laisse une trace ---'
select case when message like 'référence TA_REFERENCE_FNE -> aucune%'
            then 'OK     le retrait de référence est tracé'
            else format('ÉCHEC — trace inattendue : %s', coalesce(message, 'aucune')) end
  from certification_evenements
 where certification_id = (select id from certifications where identite = '0/6/1060')
 order by id desc limit 1;

\echo '--- La source par défaut n''affirme rien ---'
insert into certifications (dossier_id, identite, piece, etat, environnement, reference_fne)
values ('22222222-2222-2222-2222-222222222222', '0/6/1063', '1063', 'certified',
        'test', 'REF-1063');

select case when source = 'inconnue'
            then 'OK     une source non renseignée reste inconnue'
            else format('ÉCHEC — le défaut affirme « %s »', source) end
  from certifications where identite = '0/6/1063';

\echo '--- Requalifier sur preuves internes ---'
insert into certifications (dossier_id, identite, piece, etat, environnement,
                            reference_fne, erreur)
values ('22222222-2222-2222-2222-222222222222', '0/6/1064', '1064', 'certified',
        'test', 'TA_REFERENCE_FNE',
        'Réconciliation manuelle du 31/08/2026 à 22:42. Certification constatée sur le portail DGI par l''exploitant, non observée par le middleware.');

select case when requalifier_source((select id from certifications where identite = '0/6/1064'))
                 = 'reconciliation_manuelle'
            then 'OK     l''attestation complète requalifie l''entrée'
            else 'ÉCHEC — requalification refusée' end;

-- Rien d'autre n'a bougé : c'est toute la prudence de l'opération.
select case when etat = 'certified' and reference_fne = 'TA_REFERENCE_FNE'
                 and identite = '0/6/1064' and reconciliee_le is not null
            then 'OK     la requalification ne touche que la source'
            else 'ÉCHEC — la requalification a altéré autre chose' end
  from certifications where identite = '0/6/1064';

-- Et la référence fautive devient retirable, ce qui était le but.
update certifications set reference_fne = null, motif = 'Aucune référence au portail.'
 where identite = '0/6/1064';

select case when reference_fne is null and etat = 'certified'
            then 'OK     la fausse référence part une fois la source établie'
            else 'ÉCHEC — la référence n''a pas pu être retirée' end
  from certifications where identite = '0/6/1064';

\echo '--- Ce que la requalification refuse ---'
select attendre_echec($$
  select requalifier_source((select id from certifications where identite = '0/6/1063'))$$,
  'une requalification sans attestation');

select attendre_echec($$
  select requalifier_source((select id from certifications where identite = '0/6/1064'))$$,
  'la requalification d''une ligne qui se déclare déjà');

insert into certifications (dossier_id, identite, piece, etat, environnement, erreur)
values ('22222222-2222-2222-2222-222222222222', '0/6/1065', '1065', 'error',
        'test',
        'Réconciliation manuelle du 31/08/2026. Certification constatée sur le portail DGI par l''exploitant, non observée par le middleware.');

select attendre_echec($$
  select requalifier_source((select id from certifications where identite = '0/6/1065'))$$,
  'la requalification d''une entrée non certifiée');

\echo '--- Le journal des tentatives ---'
insert into certifications (dossier_id, id, identite, piece, etat, environnement)
values ('22222222-2222-2222-2222-222222222222',
        '55555555-5555-5555-5555-555555555555', '0/6/1072', '1072', 'sending', 'test');

insert into certification_tentatives (certification_id, genre, detail)
values ('55555555-5555-5555-5555-555555555555', 'envoi', 'POST n° 1');
insert into certification_tentatives (certification_id, genre, code_http, detail)
values ('55555555-5555-5555-5555-555555555555', 'reponse', 500, 'issue inconnue');
insert into certification_tentatives (certification_id, genre, detail)
values ('55555555-5555-5555-5555-555555555555', 'decision', 'non certifiée — portail consulté trop tôt');
insert into certification_tentatives (certification_id, genre, detail)
values ('55555555-5555-5555-5555-555555555555', 'envoi', 'POST n° 2');
insert into certification_tentatives (certification_id, genre, code_http, detail)
values ('55555555-5555-5555-5555-555555555555', 'reponse', 500, 'issue inconnue');

select case when count(*) = 5 then 'OK     les cinq étapes sont journalisées'
            else format('ÉCHEC — %s entrée(s)', count(*)) end
  from certification_tentatives
 where certification_id = '55555555-5555-5555-5555-555555555555';

select case when envois_partis('55555555-5555-5555-5555-555555555555') = 2
            then 'OK     deux envois comptés'
            else format('ÉCHEC — %s envoi(s) comptés',
                        envois_partis('55555555-5555-5555-5555-555555555555')) end;

select case when count(*) = 1 then 'OK     la pièce ressort comme doublon possible'
            else format('ÉCHEC — %s ligne(s) dans la vue', count(*)) end
  from certifications_multi_envois where identite = '0/6/1072';

\echo '--- Rien ne s''y réécrit ---'
select attendre_echec($$
  update certification_tentatives set detail = 'autre chose'
   where certification_id = '55555555-5555-5555-5555-555555555555'$$,
  'la réécriture d''une tentative');

select attendre_echec($$
  delete from certification_tentatives
   where certification_id = '55555555-5555-5555-5555-555555555555'$$,
  'la suppression d''une tentative');

\echo '--- Un fait observé ne se date pas dans le passé ---'
select attendre_echec($$
  insert into certification_tentatives (certification_id, genre, survenu_le, detail)
  values ('55555555-5555-5555-5555-555555555555', 'envoi', now() - interval '2 hours', 'antidaté')$$,
  'un envoi observé daté d''il y a deux heures');

-- Une reconstitution, elle, porte la date des faits : c'est tout son objet.
insert into certification_tentatives (certification_id, genre, survenu_le, code_http, detail)
values ('55555555-5555-5555-5555-555555555555', 'reconstitue',
        now() - interval '2 hours', 500, 'POST antérieur au journal, saisi après coup');

select case when count(*) = 1 then 'OK     une reconstitution peut porter une date passée'
            else 'ÉCHEC — la reconstitution a été refusée' end
  from certification_tentatives
 where certification_id = '55555555-5555-5555-5555-555555555555'
   and genre = 'reconstitue';

-- Et elle se distingue pour toujours d'un fait observé.
select case when count(*) = 5 then 'OK     les faits observés restent distincts des reconstitutions'
            else format('ÉCHEC — %s fait(s) observé(s)', count(*)) end
  from certification_tentatives
 where certification_id = '55555555-5555-5555-5555-555555555555'
   and genre <> 'reconstitue';

\echo '--- Le journal se lit dans l''ordre des faits ---'
select case when survenu_le = min(survenu_le) over () then 'OK     la reconstitution se range en tête'
            else 'ÉCHEC — ordre chronologique faux' end
  from certification_tentatives
 where certification_id = '55555555-5555-5555-5555-555555555555'
 order by survenu_le limit 1;

-- ---------------------------------------------------------------------------
-- Les demandes venues de l'écran distant
--
-- Une demande dit « quelqu'un a cliqué », jamais « certifie ». Ce que la base
-- garantit ici, c'est qu'elle ne puisse pas devenir un ordre en cours de route.
-- ---------------------------------------------------------------------------

\echo '--- Une demande ne se réécrit pas ---'

insert into demandes_certification (id, dossier_id, identite, piece, mode_paiement, demande_par)
values ('66666666-6666-6666-6666-666666666666',
        '22222222-2222-2222-2222-222222222222', '0/6/1225', '1225', 'cash',
        '77777777-7777-7777-7777-777777777777');

select attendre_echec($$
  update demandes_certification set piece = '9999'
   where id = '66666666-6666-6666-6666-666666666666'$$,
  'changer la pièce d''une demande déjà posée');

select attendre_echec($$
  update demandes_certification set mode_paiement = 'transfer'
   where id = '66666666-6666-6666-6666-666666666666'$$,
  'changer le mode de règlement après le clic');

\echo '--- Un mode de règlement inventé n''entre pas ---'
select attendre_echec($$
  insert into demandes_certification (dossier_id, identite, piece, mode_paiement, demande_par)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1', '1', 'especes',
          '77777777-7777-7777-7777-777777777777')$$,
  'un libellé français là où la DGI attend un code');

\echo '--- Deux clics ne font pas deux demandes ---'
select attendre_echec($$
  insert into demandes_certification (dossier_id, identite, piece, mode_paiement, demande_par)
  values ('22222222-2222-2222-2222-222222222222', '0/6/1225', '1225', 'cash',
          '77777777-7777-7777-7777-777777777777')$$,
  'une seconde demande sur une pièce déjà en file');

\echo '--- Une demande tranchée ne se rouvre pas ---'
update demandes_certification set etat = 'prise'
 where id = '66666666-6666-6666-6666-666666666666';
update demandes_certification set etat = 'refusee', resultat = 'pièce bloquée : TVA_ABSENTE'
 where id = '66666666-6666-6666-6666-666666666666';

select attendre_echec($$
  update demandes_certification set etat = 'en_attente'
   where id = '66666666-6666-6666-6666-666666666666'$$,
  'rouvrir une demande déjà tranchée');

select case when traitee_le is not null then 'OK     la date de traitement est posée d''office'
            else 'ÉCHEC — traitee_le est resté nul' end
  from demandes_certification
 where id = '66666666-6666-6666-6666-666666666666';

\echo '--- Une pièce tranchée redevient demandable ---'
-- La première demande est retombée : rien n'interdit d'en poser une autre, par
-- exemple après avoir corrigé la TVA dans Sage.
insert into demandes_certification (dossier_id, identite, piece, mode_paiement, demande_par)
values ('22222222-2222-2222-2222-222222222222', '0/6/1225', '1225', 'cash',
        '77777777-7777-7777-7777-777777777777');

select case when count(*) = 2 then 'OK     une nouvelle demande est possible après un refus'
            else format('ÉCHEC — %s demande(s)', count(*)) end
  from demandes_certification where identite = '0/6/1225';

-- ---------------------------------------------------------------------------
-- La reservation : une piece ne part qu'une fois
--
-- L'invariant n'est pas « un seul agent ». Deux agents peuvent traiter deux
-- factures differentes du meme dossier, et c'est meme souhaitable. Ce qui ne
-- doit jamais arriver, c'est que la MEME piece parte deux fois — ce qui est
-- arrive sur ce dossier, faute d'une memoire partagee entre les postes.
-- ---------------------------------------------------------------------------

\echo '--- Une piece libre se reserve ---'
select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'test',
                                '0/6/2001', '2001', 'agent-A')
            then 'OK     l''agent A reserve la piece 2001'
            else 'ÉCHEC — une piece libre a été refusée' end;

\echo '--- La meme piece ne se reserve pas deux fois ---'
select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'test',
                                '0/6/2001', '2001', 'agent-B')
            then 'ÉCHEC — deux agents ont réservé la même pièce'
            else 'OK     refusé : la pièce 2001 est déjà en vol' end;

\echo '--- Mais deux agents peuvent traiter deux pieces differentes ---'
-- Le point qui distingue cette conception d'un verrou par dossier : rien
-- n'oblige les agents a se mettre en file. Seule la piece est exclusive.
select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'test',
                                '0/6/2002', '2002', 'agent-B')
            then 'OK     l''agent B réserve la pièce 2002 pendant que A tient 2001'
            else 'ÉCHEC — un second agent a été bloqué sur une autre pièce' end;

select case when count(*) = 2 then 'OK     deux réservations coexistent'
            else format('ÉCHEC — %s réservation(s)', count(*)) end
  from certifications where piece in ('2001', '2002') and etat = 'sending';

\echo '--- Une piece certifiee ne se reserve jamais ---'
update certifications
   set etat = 'certified', reference_fne = '2304903U26000002001', certifiee_le = now()
 where identite = '0/6/2001';

select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'test',
                                '0/6/2001', '2001', 'agent-A')
            then 'ÉCHEC — une pièce certifiée a pu repartir'
            else 'OK     refusé : la DGI l''a déjà enregistrée' end;

\echo '--- Un refus NET rend la piece, une issue inconnue non ---'
select case when liberer_piece('22222222-2222-2222-2222-222222222222', 'test',
                               '0/6/2002', 'refus net : 400 Bad Request')
            then 'OK     la pièce 2002 est rendue après un refus net'
            else 'ÉCHEC — la libération a échoué' end;

select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'test',
                                '0/6/2002', '2002', 'agent-A')
            then 'OK     une pièce rendue peut repartir'
            else 'ÉCHEC — une pièce rendue reste bloquée' end;

-- Elle est de nouveau en vol : la liberer une seconde fois n'a pas de sens,
-- mais surtout, liberer ce qui n'est pas reserve ne doit rien faire.
select case when liberer_piece('22222222-2222-2222-2222-222222222222', 'test',
                               '0/6/2001', 'tentative sur une pièce certifiée')
            then 'ÉCHEC — une pièce certifiée a été rendue'
            else 'OK     refusé : on ne libère que ce qui est en vol' end;

\echo '--- L''environnement separe les essais de la production ---'
-- Une piece certifiee en essai ne doit pas empecher la meme piece de partir en
-- production : ce sont deux plateformes, deux histoires.
select case when reserver_piece('22222222-2222-2222-2222-222222222222', 'production',
                                '0/6/2001', '2001', 'agent-A')
            then 'OK     la production a sa propre histoire'
            else 'ÉCHEC — l''essai bloque la production' end;

\echo '--- La supervision : un agent, une ligne, plusieurs agents par dossier ---'
insert into battements (dossier_id, agent_id, poste, environnement, mode, examinees, envoyees)
values ('22222222-2222-2222-2222-222222222222', 'agent-A', 'POSTE-COMPTA', 'test', 'Manual', 14, 2),
       ('22222222-2222-2222-2222-222222222222', 'agent-B', 'POSTE-DIRECTION', 'test', 'Manual', 9, 1);

select case when count(*) = 2 then 'OK     deux agents du même dossier ont chacun leur ligne'
            else format('ÉCHEC — %s ligne(s)', count(*)) end
  from battements where dossier_id = '22222222-2222-2222-2222-222222222222';

insert into battements (dossier_id, agent_id, environnement, mode, examinees, envoyees)
values ('22222222-2222-2222-2222-222222222222', 'agent-A', 'test', 'Manual', 28, 3)
on conflict (dossier_id, agent_id) do update
   set quand = now(), examinees = excluded.examinees, envoyees = excluded.envoyees;

select case when examinees = 28 then 'OK     le battement se remplace, il ne s''empile pas'
            else format('ÉCHEC — examinees = %s', examinees) end
  from battements where agent_id = 'agent-A';

\echo '--- Un agent qui se tait finit par se voir ---'
update battements set quand = now() - interval '20 minutes' where agent_id = 'agent-A';

select case when count(*) = 1 then 'OK     l''agent muet remonte dans la vue'
            else 'ÉCHEC — un agent muet reste invisible' end
  from agents_muets where agent_id = 'agent-A';
