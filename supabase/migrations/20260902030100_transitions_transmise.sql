-- ---------------------------------------------------------------------------
-- Ce que « transmise » autorise, et ce qu'elle interdit
--
-- Dans un fichier à part : PostgreSQL refuse d'employer une valeur d'énumération
-- dans la transaction qui vient de l'ajouter.
-- ---------------------------------------------------------------------------

create or replace function transition_autorisee(avant fne_etat, apres fne_etat)
returns boolean language sql immutable as $$
  select case
    -- Une pièce certifiée l'est pour de bon. La DGI a enregistré la facture :
    -- aucun état ultérieur ne peut le défaire, et un renvoi serait un doublon.
    when avant = 'certified' then apres = 'certified'

    -- Un envoi parti dont on ignore l'issue ne redevient pas « prêt » tout
    -- seul : ce serait autoriser un second envoi sans avoir vérifié que le
    -- premier n'a pas abouti. Le retour se fait par debloquer_envoi().
    when avant = 'sending' then apres in ('sending', 'transmise', 'certified', 'error')

    -- Déposée au portail. Elle attend un clic, pas un renvoi : la seule suite
    -- normale est la certification. Le retour en « error » reste possible —
    -- un opérateur peut s'être trompé de facture au portail — mais il dédit un
    -- constat, et le middleware le signale au moment de l'inscrire.
    --
    -- Ce qu'elle ne peut pas devenir, et c'est tout l'objet : « pending »,
    -- « validating » ou « ready », c'est-à-dire renvoyable.
    when avant = 'transmise' then apres in ('transmise', 'certified', 'error')

    else true
  end;
$$;

comment on function transition_autorisee is
  'Trois murs : certified est terminal, sending et transmise ne se relâchent pas en silence.';

-- --- Les pièces déposées, en attente de clic --------------------------------

create or replace view certifications_au_portail as
  select c.id, c.dossier_id, c.environnement, c.identite, c.piece,
         c.envoyee_le, c.dernier_code_http,
         now() - coalesce(c.envoyee_le, c.cree_le) as depuis
    from certifications c
   where c.etat = 'transmise'
   order by c.envoyee_le;

comment on view certifications_au_portail is
  'Les factures déposées chez la DGI qui attendent le clic qui les certifiera. Elles ne repartent pas : elles y sont déjà.';
