-- ---------------------------------------------------------------------------
-- Ce que la base refuse, quoi qu'en dise l'applicatif.
--
-- Une facture certifiée deux fois ne se rattrape que par un avoir. Les
-- garanties qui l'empêchent ne peuvent pas dépendre du seul code appelant :
-- elles vivent ici.
-- ---------------------------------------------------------------------------

-- --- Horodatage -------------------------------------------------------------

create or replace function toucher_maj_le() returns trigger
language plpgsql as $$
begin
  new.maj_le := now();
  return new;
end;
$$;

create trigger dossiers_maj_le before update on dossiers
  for each row execute function toucher_maj_le();

create trigger certifications_maj_le before update on certifications
  for each row execute function toucher_maj_le();

-- --- La machine à états -----------------------------------------------------

create or replace function transition_autorisee(avant fne_etat, apres fne_etat)
returns boolean language sql immutable as $$
  select case
    -- Une pièce certifiée l'est pour de bon. La DGI a enregistré la facture :
    -- aucun état ultérieur ne peut le défaire, et un renvoi serait un doublon.
    when avant = 'certified' then apres = 'certified'

    -- Un envoi parti dont on ignore l'issue ne redevient pas « prêt » tout
    -- seul : ce serait autoriser un second envoi sans avoir vérifié que le
    -- premier n'a pas abouti. Le retour se fait par debloquer_envoi().
    when avant = 'sending' then apres in ('sending', 'certified', 'error')

    else true
  end;
$$;

comment on function transition_autorisee is
  'Les deux seuls murs : certified est terminal, sending ne se relâche pas en silence.';

create or replace function verifier_transition() returns trigger
language plpgsql as $$
begin
  if tg_op = 'UPDATE' and new.etat is distinct from old.etat
     and not transition_autorisee(old.etat, new.etat) then
    raise exception
      'Transition % vers % interdite sur la pièce % (%).',
      old.etat, new.etat, old.piece, old.identite
      using hint = case
        when old.etat = 'certified'
          then 'La DGI a certifié cette facture. Une correction passe par un avoir, pas par un renvoi.'
        else 'Envoi en cours dont l''issue est inconnue. Vérifiez sur le portail DGI, puis appelez debloquer_envoi().'
      end;
  end if;

  -- La référence d'une pièce certifiée ne s'efface pas.
  if tg_op = 'UPDATE' and old.etat = 'certified'
     and btrim(coalesce(new.reference_fne, '')) <> btrim(old.reference_fne) then
    raise exception 'La référence FNE de la pièce % ne peut pas être modifiée.', old.piece;
  end if;

  if new.etat = 'sending' and new.envoyee_le is null then
    new.envoyee_le := now();
  end if;

  if new.etat = 'certified' and new.certifiee_le is null then
    new.certifiee_le := now();
  end if;

  return new;
end;
$$;

create trigger certifications_transition before insert or update on certifications
  for each row execute function verifier_transition();

-- --- La trace, écrite par la base et non par l'appelant ---------------------

create or replace function tracer_etat() returns trigger
language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    insert into certification_evenements (certification_id, etat_avant, etat_apres, message, corps)
    values (new.id, null, new.etat, 'création', new.reponse);
  elsif new.etat is distinct from old.etat then
    insert into certification_evenements (certification_id, etat_avant, etat_apres, message, corps)
    values (new.id, old.etat, new.etat, coalesce(nullif(new.erreur, ''), ''), new.reponse);
  end if;

  return null;
end;
$$;

create trigger certifications_tracer after insert or update on certifications
  for each row execute function tracer_etat();

-- L'historique ne se réécrit pas.
create or replace function refuser_reecriture() returns trigger
language plpgsql as $$
begin
  raise exception 'certification_evenements est en ajout seul : ni modification ni suppression.';
end;
$$;

create trigger evenements_immuables before update or delete on certification_evenements
  for each row execute function refuser_reecriture();

-- --- La sortie nommée de « sending » ----------------------------------------

create or replace function debloquer_envoi(
  p_certification uuid,
  p_motif         text,
  p_certifiee     boolean default false,
  p_reference     text default null
) returns certifications
language plpgsql security invoker as $$
declare
  resultat certifications;
begin
  if btrim(coalesce(p_motif, '')) = '' then
    raise exception 'Un motif est exigé : dire ce qui a été vérifié sur le portail DGI.';
  end if;

  if p_certifiee and btrim(coalesce(p_reference, '')) = '' then
    raise exception 'Une pièce déclarée certifiée doit porter sa référence.';
  end if;

  -- On passe par 'error' pour que la machine à états autorise le retour, et la
  -- trace garde le motif de la décision humaine.
  update certifications
     set etat = 'error', erreur = p_motif
   where id = p_certification and etat = 'sending';

  if not found then
    raise exception 'La pièce % n''est pas en attente d''issue.', p_certification;
  end if;

  if p_certifiee then
    update certifications
       set etat = 'certified', reference_fne = p_reference, erreur = ''
     where id = p_certification
    returning * into resultat;
  else
    select * into resultat from certifications where id = p_certification;
  end if;

  return resultat;
end;
$$;

comment on function debloquer_envoi is
  'Seule sortie de « sending », après vérification humaine sur le portail DGI. Le motif est obligatoire et tracé.';
