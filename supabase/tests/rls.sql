-- Verification du schema et de l'isolation entre dossiers.
--
-- Chaque scenario se joue sous le role `authenticated`, comme le fait l'API
-- Supabase : la RLS s'applique donc reellement, contrairement a une execution
-- sous le proprietaire des tables.

\set ON_ERROR_STOP on

-- Deux comptables de deux entreprises differentes.
insert into auth.users (id, email) values
  ('11111111-1111-4111-8111-111111111111', 'aya@fish-afric.test'),
  ('22222222-2222-4222-8222-222222222222', 'kone@autre-societe.test');

create or replace function tests.verifie(condition boolean, libelle text)
returns void language plpgsql as $$
begin
  if condition then raise notice '  ok   %', libelle;
  else raise exception 'ECHEC : %', libelle;
  end if;
end;
$$;

-- Joue une requete et retourne le code d'erreur SQL, ou 'aucune'.
create or replace function tests.erreur(requete text)
returns text language plpgsql as $$
begin
  execute requete;
  return 'aucune';
exception when others then
  return sqlstate;
end;
$$;

do $$
declare
  aya uuid := '11111111-1111-4111-8111-111111111111';
  kone uuid := '22222222-2222-4222-8222-222222222222';
  dossier_aya uuid;
  dossier_kone uuid;
  conversion uuid;
  nombre integer;
  code text;
begin
  set local role authenticated;

  -- 1. Creation d'un dossier : le createur en devient gestionnaire.
  perform set_config('test.user_id', aya::text, true);
  dossier_aya := (public.creer_dossier('FISH-AFRIC', '2304903U')).id;
  perform tests.verifie(public.est_gestionnaire(dossier_aya), 'le createur est gestionnaire');
  perform tests.verifie(
    (select count(*) from public.parametres where dossier_id = dossier_aya) = 1,
    'les parametres par defaut sont poses');

  perform set_config('test.user_id', kone::text, true);
  dossier_kone := (public.creer_dossier('AUTRE SOCIETE', '9988776C')).id;

  -- 2. Isolation : chacun ne voit que son dossier.
  perform set_config('test.user_id', aya::text, true);
  select count(*) into nombre from public.dossiers;
  perform tests.verifie(nombre = 1, 'un membre ne voit que son dossier');
  perform tests.verifie(
    (select count(*) from public.dossiers where id = dossier_kone) = 0,
    'le dossier d''un tiers est invisible');

  -- 3. Ecrire dans le dossier d'un tiers est refuse.
  code := tests.erreur(format(
    'insert into public.comptes_tiers (dossier_id, ncc, compte_sage) values (%L, %L, %L)',
    dossier_kone, '5011806N', '411FRAUDE'));
  perform tests.verifie(code = '42501', 'ecrire chez un tiers est refuse par la RLS');

  -- 4. Correspondance clients : un NCC ne pointe que sur un compte.
  insert into public.comptes_tiers (dossier_id, ncc, nom, compte_sage)
  values (dossier_aya, '5011806N', 'PROSUMA-STE IVOIRIENNE', '411PROSUMA');

  code := tests.erreur(format(
    'insert into public.comptes_tiers (dossier_id, ncc, nom, compte_sage) values (%L, %L, %L, %L)',
    dossier_aya, '5011806n', 'PROSUMA', '411AUTRE'));
  perform tests.verifie(code = '23505', 'un NCC en double est refuse, casse comprise');

  -- Un client sans NCC (B2C) reste identifie par son nom.
  insert into public.comptes_tiers (dossier_id, nom, compte_sage)
  values (dossier_aya, 'MOUSSA FOFANA', '411DIVERS');
  code := tests.erreur(format(
    'insert into public.comptes_tiers (dossier_id, nom, compte_sage) values (%L, %L, %L)',
    dossier_aya, 'moussa fofana', '411AUTRE'));
  perform tests.verifie(code = '23505', 'un client sans NCC est unique par son nom');

  code := tests.erreur(format(
    'insert into public.comptes_tiers (dossier_id, compte_sage) values (%L, %L)',
    dossier_aya, '411VIDE'));
  perform tests.verifie(code = '23514', 'un client sans NCC ni nom est refuse');

  -- 5. Historique et detection des doublons.
  insert into public.conversions (
    dossier_id, utilisateur_id, fichier, source, profil_id,
    nb_factures, nb_avoirs, nb_lignes, total_ht, total_tva, total_ttc)
  values (
    dossier_aya, aya, 'factures_20260811.json', 'fne-json', 'sage100-import-export',
    1, 1, 2, 43091.06, 7756.38, 50847.44)
  returning id into conversion;

  insert into public.factures (
    dossier_id, conversion_id, reference_fne, numero_piece, nature,
    date_facture, client_ncc, client_nom, compte_sage, total_ht, total_tva, total_ttc)
  values
    (dossier_aya, conversion, '2304903U26000000889', '26000000889', 'FACTURE',
     '2026-08-11', '5011806N', 'PROSUMA-STE IVOIRIENNE', '411PROSUMA', 21545.53, 3878.19, 25423.72),
    (dossier_aya, conversion, 'A2304903U2600000038', 'A2600000038', 'AVOIR',
     '2026-08-10', '2114866J', 'COTE D''IVOIRE SUPERMARCHES', '411CIS', 21545.53, 3878.19, 25423.72);

  code := tests.erreur(format(
    'insert into public.factures (dossier_id, conversion_id, reference_fne, nature)
     values (%L, %L, %L, %L)',
    dossier_aya, conversion, '2304903U26000000889', 'FACTURE'));
  perform tests.verifie(code = '23505', 'une facture FNE ne peut entrer qu''une fois');

  select count(*) into nombre
  from public.references_deja_importees(
    dossier_aya,
    array['2304903U26000000889', '2304903U26000000999']);
  perform tests.verifie(nombre = 1, 'seules les references deja importees remontent');

  -- La meme reference reste possible dans un autre dossier.
  perform set_config('test.user_id', kone::text, true);
  insert into public.conversions (dossier_id, utilisateur_id, fichier, source, profil_id)
  values (dossier_kone, kone, 'autre.json', 'fne-json', 'sage100-import-export')
  returning id into conversion;
  insert into public.factures (dossier_id, conversion_id, reference_fne, nature)
  values (dossier_kone, conversion, '2304903U26000000889', 'FACTURE');
  perform tests.verifie(true, 'la meme reference est acceptee dans un autre dossier');

  -- 6. Les factures d'un tiers restent invisibles.
  select count(*) into nombre from public.factures;
  perform tests.verifie(nombre = 1, 'un membre ne voit que les factures de son dossier');

  -- 7. Une conversion doit etre attribuee a son auteur.
  code := tests.erreur(format(
    'insert into public.conversions (dossier_id, utilisateur_id, fichier, source, profil_id)
     values (%L, %L, %L, %L, %L)',
    dossier_kone, aya, 'usurpation.json', 'fne-json', 'sage100-import-export'));
  perform tests.verifie(code = '42501', 'une conversion ne peut etre attribuee a un autre');

  -- 8. Enregistrement groupe de la table clients.
  perform set_config('test.user_id', aya::text, true);
  select public.enregistrer_comptes_tiers(dossier_aya, '[
    {"ncc": "0821614U", "nom": "STE DE BOULANGERIE", "compte_sage": "411SBV"},
    {"ncc": "2114866J", "nom": "COTE D IVOIRE SUPERMARCHES", "compte_sage": "411CIS"},
    {"ncc": "9999999X", "nom": "SANS COMPTE", "compte_sage": ""}
  ]'::jsonb) into nombre;
  perform tests.verifie(nombre = 2, 'les entrees sans compte sont ignorees');

  -- 9. Le tableau de bord ne montre que les dossiers accessibles.
  select count(*) into nombre from public.tableau_de_bord;
  perform tests.verifie(nombre = 1, 'le tableau de bord respecte la RLS');

  raise notice 'Tous les controles sont passes.';
end;
$$;
