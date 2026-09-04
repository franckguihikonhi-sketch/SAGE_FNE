-- ---------------------------------------------------------------------------
-- Une certification peut ne porter aucune référence
--
-- La plateforme d'essai de la DGI certifie des factures sans publier de
-- référence exploitable : ni le PDF ni la fiche du portail n'en montrent. La
-- contrainte posée jusqu'ici — « certifiée implique une référence » — était
-- donc fausse, et elle poussait à la faute : pour inscrire une certification
-- bien réelle, il fallait donner une référence, donc en inventer une. C'est
-- arrivé, avec une valeur d'exemple recopiée telle quelle.
--
-- Une référence inventée est pire que pas de référence : elle désigne chez la
-- DGI une facture qui n'existe pas. La règle change donc de forme. Ce qui est
-- exigé d'une certification, ce n'est pas un numéro — c'est de savoir d'où
-- vient ce qu'on affirme.
-- ---------------------------------------------------------------------------

-- --- NULL plutôt que la chaîne vide -----------------------------------------

-- '' et NULL disaient la même chose sans le dire pareil. NULL est le seul des
-- deux qui distingue « la plateforme n'en publie pas » de « personne n'a
-- encore regardé ».
alter table certifications
  alter column reference_fne drop not null,
  alter column reference_fne drop default,
  alter column token         drop not null,
  alter column token         drop default;

update certifications set reference_fne = null where btrim(reference_fne) = '';
update certifications set token         = null where btrim(token) = '';

-- Une chaîne vide ne doit pas revenir par la porte de derrière.
alter table certifications
  add constraint certification_reference_non_vide
    check (reference_fne is null or length(btrim(reference_fne)) > 0),
  add constraint certification_token_non_vide
    check (token is null or length(btrim(token)) > 0);

comment on column certifications.reference_fne is
  'Référence publiée par la DGI. NULL quand la plateforme n''en publie aucune : cela arrive, et n''ôte rien à la certification.';

-- --- Ce qu'on exige vraiment d'une certification ----------------------------

alter table certifications
  add column motif text not null default '';

comment on column certifications.motif is
  'Ce qu''un humain a déclaré, et pourquoi. Les corrections s''y ajoutent sans effacer les précédentes.';

alter table certifications
  drop constraint certification_certifiee_a_une_reference;

-- Une certification observée par le middleware porte forcément la référence
-- que la DGI a renvoyée : ne pas l'avoir signifierait qu'on a mal lu la
-- réponse. Une certification constatée à la main peut n'en avoir aucune, mais
-- doit alors dire pourquoi.
alter table certifications
  add constraint certification_certifiee_est_justifiee
    check (
      etat <> 'certified'
      or reference_fne is not null
      or (source = 'reconciliation_manuelle' and length(btrim(motif)) > 0)
    );

comment on constraint certification_certifiee_est_justifiee on certifications is
  'Une référence, ou un motif expliquant son absence. Jamais ni l''un ni l''autre : ce serait une certification que rien ne fonde.';

-- --- L'unicité ne dépend pas de la référence --------------------------------

-- Elle porte sur (dossier, environnement, identité) et rien d'autre. Une
-- facture certifiée sans référence bloque le renvoi exactement comme les
-- autres : c'est l'identité Sage qui fait foi, jamais le numéro de la DGI.
-- Rien à changer ici — cette vue le rend vérifiable.
create or replace view certifications_sans_reference as
  select c.id, c.dossier_id, c.environnement, c.identite, c.piece,
         c.certifiee_le, c.source, c.motif
    from certifications c
   where c.etat = 'certified' and c.reference_fne is null;

comment on view certifications_sans_reference is
  'Les certifications qu''aucun numéro ne permet de retrouver chez la DGI. À rapprocher du portail quand il en publiera.';

-- --- Retirer une fausse référence, sans jamais en substituer une ------------

-- Le déclencheur d'origine interdisait toute modification de la référence d'une
-- pièce certifiée. La règle était trop large : elle scellait aussi les
-- références fautives, et une référence inventée scellée est un mensonge que
-- rien ne peut plus corriger.
--
-- Ce qui doit rester interdit, c'est de remplacer une référence par une autre —
-- cela ferait pointer la ligne vers une autre facture chez la DGI. Le retrait,
-- lui, n'affirme plus rien : il rétablit ce que la plateforme montre vraiment.
-- Et il ne se conçoit que sur une réconciliation manuelle : une référence lue
-- dans la réponse de la DGI n'est pas une déclaration humaine, elle ne se
-- retire pas.
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

  if tg_op = 'UPDATE' and old.etat = 'certified'
     and new.reference_fne is distinct from old.reference_fne then

    -- Une référence contre une autre : jamais. La ligne désignerait une autre
    -- facture chez la DGI.
    if new.reference_fne is not null and old.reference_fne is not null then
      raise exception
        'La référence FNE de la pièce % ne peut pas être remplacée (% -> %).',
        old.piece, old.reference_fne, new.reference_fne
        using hint = 'Une référence erronée se retire (reference_fne = null), elle ne se substitue pas.';
    end if;

    -- En poser une là où il n'y en avait pas, c'est réconcilier : le motif doit
    -- le dire.
    if old.reference_fne is null and length(btrim(coalesce(new.motif, ''))) = 0 then
      raise exception
        'Poser une référence sur la pièce % demande un motif.', old.piece;
    end if;

    -- La retirer ne se conçoit que sur une déclaration humaine.
    if new.reference_fne is null and old.source <> 'reconciliation_manuelle' then
      raise exception
        'La référence de la pièce % vient de la DGI et ne se retire pas.', old.piece
        using hint = 'Seule une réconciliation manuelle peut être corrigée : elle repose sur une lecture, pas sur une réponse.';
    end if;

    if new.reference_fne is null and length(btrim(coalesce(new.motif, ''))) = 0 then
      raise exception
        'Retirer la référence de la pièce % demande un motif.', old.piece;
    end if;
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

-- Une correction de référence laisse une trace, comme un changement d'état.
create or replace function tracer_etat() returns trigger
language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    insert into certification_evenements (certification_id, etat_avant, etat_apres, message, corps)
    values (new.id, null, new.etat, 'création', new.reponse);
  elsif new.etat is distinct from old.etat then
    insert into certification_evenements (certification_id, etat_avant, etat_apres, message, corps)
    values (new.id, old.etat, new.etat, coalesce(nullif(new.erreur, ''), ''), new.reponse);
  elsif new.reference_fne is distinct from old.reference_fne then
    insert into certification_evenements (certification_id, etat_avant, etat_apres, message, corps)
    values (new.id, old.etat, new.etat,
            format('référence %s -> %s. %s',
                   coalesce(old.reference_fne, 'aucune'),
                   coalesce(new.reference_fne, 'aucune'),
                   coalesce(new.motif, '')),
            new.reponse);
  end if;

  return null;
end;
$$;
