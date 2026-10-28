# Annexe C — Checklists

## C.1 Avant d'écrire du code

- [ ] Contenus du domaine public, sous licence libre ou publiés par les ayants droit (preuve ou mention notée)
- [ ] Conditions d'utilisation et `robots.txt` lus ; accès automatisé admis
- [ ] Aucune protection à contourner (CAPTCHA, vérification de navigateur, authentification)
- [ ] HTTPS disponible pour les pages **et** les `.torrent`
- [ ] Hôtes exacts identifiés (y compris ceux des redirections)
- [ ] Capacités réellement observées listées ; champs absents listés
- [ ] Statuts HTTP et dates notés ; 2 réponses réelles enregistrées comme fixtures
- [ ] Verdict écrit : intégrer / désactivée par défaut / rejeter

## C.2 Avant fusion

- [ ] Connecteur hérite de `ConnectorBase`, toutes les requêtes via `Http.GetAsync`
- [ ] Aucune exception non classée ; formats inattendus → `MalformedResponse`
- [ ] Aucune valeur inventée (seeders, année, taille, licence) ; capacités honnêtes
- [ ] Hôtes autorisés exacts, sans joker inutile ni hôte tiers
- [ ] Requêtes bornées (pages, taille, quota), cache des données stables
- [ ] Regex avec délai d'exécution / XML sans DTD / JSON lu depuis un tampon borné
- [ ] `ValidateConfiguration` vérifie ce qui rendrait la source inutilisable
- [ ] `ProbeAsync` vérifie le **format**, pas seulement le code HTTP
- [ ] Semis : source **désactivée** par défaut, notes et limites renseignées
- [ ] Tests unitaires sur fixtures réelles ; tests d'intégration sur serveur simulé ; `dotnet test TorrentSearch.slnx` vert
- [ ] `docs/sources.md`, `README.md`, `docs/testing.md`, `docs/progress.md` à jour
- [ ] Revue de sécurité ciblée faite (chapitre 5.4)

## C.3 Avant activation

- [ ] « Tester » depuis l'administration : Saine ou Dégradée, sans avertissement inexpliqué
- [ ] Recherche manuelle : résultats cohérents, informations absentes affichées « inconnu »
- [ ] Récupération d'un `.torrent` : validée, info-hash affiché, aucun avertissement de fichier dangereux
- [ ] Quota et délai adaptés aux règles de la source
- [ ] Limites lues et acceptées par l'administrateur (licence, couverture)
