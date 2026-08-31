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
select case when count(*) = 7 then 'OK     RLS active sur les 7 tables'
            else format('ÉCHEC — RLS active sur %s tables seulement', count(*)) end
  from pg_tables where schemaname = 'public' and rowsecurity;

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
