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
insert into regles_tva_zero (dossier_id, portee, cle, regime)
values ('22222222-2222-2222-2222-222222222222', 'dossier', '', 'legal_exemption_tee_rme');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, portee, cle, regime)
  values ('22222222-2222-2222-2222-222222222222', 'article', '', 'conventional_exemption')$$,
  'une règle d''article sans clé');

select attendre_echec($$
  insert into regles_tva_zero (dossier_id, portee, cle, regime)
  values ('22222222-2222-2222-2222-222222222222', 'dossier', '13415001', 'conventional_exemption')$$,
  'une règle de dossier avec une clé');

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
