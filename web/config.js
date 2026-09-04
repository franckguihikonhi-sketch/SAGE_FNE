// Ce que cette page a besoin de savoir pour joindre votre projet Supabase.
//
// Ces deux valeurs ne sont PAS des secrets. La clé « anon » est faite pour
// vivre dans un navigateur : c'est la RLS, côté base, qui décide de ce que
// chaque compte voit. La clé de service, elle, n'a rien à faire ici — elle
// vit sur le poste de l'agent, en variable machine.
//
// Copiez ce fichier en « config.local.js » si vous préférez ne pas versionner
// vos valeurs, et changez la balise script de index.html en conséquence.
window.CONFIG_FNE = {
  url: 'A_COMPLETER',
  cleAnon: 'A_COMPLETER',
};
