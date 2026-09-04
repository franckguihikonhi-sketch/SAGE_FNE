-- ---------------------------------------------------------------------------
-- Suite de la précédente.
--
-- PostgreSQL refuse d'employer une valeur d'énumération dans la transaction qui
-- vient de l'ajouter : d'où ce second fichier, qui ne fait que s'en servir.
-- ---------------------------------------------------------------------------

alter table certifications
  alter column source set default 'inconnue';

comment on column certifications.source is
  'D''où vient ce que la ligne affirme. « inconnue » est le défaut : une source non renseignée ne doit rien affirmer.';

-- Une certification issue du middleware porte forcément une référence, et une
-- réconciliation manuelle un motif. « inconnue » ne peut donc plus satisfaire
-- une certification sans référence : il faudra la requalifier d'abord, comme
-- côté middleware.
--
-- La contrainte n'a pas à changer : elle exige déjà « reference_fne is not null
-- ou (source = reconciliation_manuelle et motif) ». Une ligne inconnue sans
-- référence est refusée, ce qui est le comportement voulu.

-- --- Requalifier ce que les preuves internes désignent ----------------------

-- Ne reclasse QUE sur l'attestation complète que la réconciliation manuelle
-- écrit, et jamais une ligne qui se déclare déjà. Rien d'autre ne bouge : ni
-- l'état, ni l'identité, ni l'empreinte, ni la référence — même fautive.
create or replace function requalifier_source(la_certification uuid)
returns source_certification
language plpgsql security invoker as $$
declare
  ligne certifications%rowtype;
  texte text;
begin
  select * into ligne from certifications where id = la_certification for update;

  if not found then
    raise exception 'Certification % introuvable.', la_certification;
  end if;

  if ligne.source <> 'inconnue' then
    raise exception
      'La certification % se déclare déjà « % » : rien n''est déduit d''une ligne qui s''annonce.',
      ligne.piece, ligne.source;
  end if;

  if ligne.etat <> 'certified' then
    raise exception
      'La certification % est en « % » : seules les certifications ont une origine à établir.',
      ligne.piece, ligne.etat;
  end if;

  texte := lower(concat_ws(' ', coalesce(ligne.erreur, ''), coalesce(ligne.motif, '')));

  if texte like '%réconciliation manuelle%'
     and texte like '%constatée sur le portail dgi par l''exploitant%'
     and texte like '%non observée par le middleware%' then

    update certifications
       set source = 'reconciliation_manuelle',
           reconciliee_le = coalesce(reconciliee_le, certifiee_le, now()),
           motif = concat_ws(E'\n', nullif(motif, ''),
                    format('Requalification du %s : source « inconnue » corrigée en ' ||
                           '« reconciliation_manuelle » sur l''attestation portée par l''entrée.',
                           to_char(now(), 'DD/MM/YYYY HH24:MI')))
     where id = la_certification;

    return 'reconciliation_manuelle';
  end if;

  raise exception
    'Rien ne permet d''établir l''origine de la certification %. Une requalification demande '
    'l''attestation complète de la réconciliation manuelle, pas un fragment.', ligne.piece;
end;
$$;

comment on function requalifier_source is
  'Établit l''origine d''une certification « inconnue » sur ses seules preuves internes. Ne conclut jamais à « middleware » : déduire une réponse de la DGI d''une absence de preuve serait refaire la faute d''origine.';
