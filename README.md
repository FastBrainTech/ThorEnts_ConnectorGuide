# ThorEnts — Guide d'implémentation d'une nouvelle source de données

Guide complet, pas à pas, pour ajouter une source à ThorEnts, illustré de bout en bout par un exemple réel :
**Public Domain Torrents** (<https://www.publicdomaintorrents.info>), un catalogue de films classiques du domaine public évalué le
28/10/2026 mais pas encore intégré au dépôt principal.

> Ce projet est **uniquement de la documentation** (Markdown + fixtures d'exemple). Le code présenté se copie dans le dépôt
> `ThorEnts` aux emplacements indiqués. Il a été écrit contre l'état du dépôt au 28/10/2026 (contrat `ITorrentSourceConnector`,
> classe `ConnectorBase`, `SourceHttpClient`).

## À qui s'adresse ce guide

Développeur C#/.NET qui connaît ASP.NET Core et EF Core, et qui doit brancher une source supplémentaire sans casser les garanties
de l'application : isolement des pannes, sécurité réseau, informations jamais inventées, respect des conditions des sources.

## Parcours

| # | Chapitre | Ce que vous produisez |
|---|---|---|
| 1 | [Évaluer la source](01-evaluation.md) | Fiche d'évaluation (licences, conditions, accès, protections, capacités), verdict |
| 2 | [Concevoir le connecteur](02-conception.md) | Cartographie des données, stratégie d'accès et de cache, capacités, codes d'erreur |
| 3 | [Implémenter](03-implementation.md) | `PublicDomainTorrentsConnector.cs` complet, enregistrement DI, semis |
| 4 | [Tester](04-tests.md) | Fixtures réelles, tests unitaires d'analyse, tests d'intégration sur serveur simulé |
| 5 | [Intégrer et livrer](05-integration.md) | Migration (si besoin), documentation `docs/sources.md`, revue de sécurité, activation progressive |
| 6 | [Exploiter](06-exploitation.md) | Réglages, surveillance, incidents types, désactivation, retrait |
| A | [Annexe A — Référence du contrat](annexe-a-contrat.md) | Types, codes d'erreur, capacités, règles d'or |
| B | [Annexe B — Gabarit minimal](annexe-b-gabarit.md) | Squelette prêt à copier pour une source JSON/RSS |
| C | [Annexe C — Checklists](annexe-c-checklists.md) | Listes de contrôle avant fusion et avant activation |

Fichiers fournis :

| Dossier | Contenu | Destination dans le dépôt `ThorEnts` |
|---|---|---|
| [`fixtures/`](fixtures/) | pages réelles du 28/10/2026, réduites | `tests/fixtures/sources/` |
| [`src/`](src/) | `PublicDomainTorrentsConnector.cs` (compilé contre le dépôt) | `src/TorrentSearch.Infrastructure/Connectors/` |
| [`tests/`](tests/) | tests unitaires (15) et d'intégration (3), exécutés avec succès | `tests/TorrentSearch.Tests.Unit/Connectors/`, `tests/TorrentSearch.Tests.Integration/` |

## Les huit règles d'or d'un connecteur ThorEnts

1. **Source autorisée uniquement** : contenus du domaine public, sous licence libre ou publiés par les ayants droit, accès
   automatisé admis. Pas de contournement de CAPTCHA, de vérification de navigateur, d'authentification ou de limites.
2. **Jamais d'exception pour une panne de source** : renvoyer un `ConnectorSearchResult.Fail(SourceErrorCode, message)`.
3. **Jamais d'accès à la base** : le connecteur reçoit un `SourceContext` et renvoie des `ReleaseCandidate`.
4. **Toutes les requêtes via `SourceHttpClient`** (hôtes autorisés, HTTPS, redirections vérifiées, SSRF, taille, quota).
5. **Rien d'inventé** : une valeur inconnue reste `null` (taille, seeders, année, licence…). Les seeders portent leur heure de collecte.
6. **Capacités honnêtes** : ne déclarer que ce que la source fournit réellement.
7. **Sobriété** : requêtes bornées, cache des données stables, respect de `Retry-After` et des règles de la source.
8. **Tests sans Internet** : fixtures réelles enregistrées + serveur HTTP local simulant pannes et anomalies.

## Résultat attendu

À la fin du guide, la source apparaît dans **Administration** (désactivée par défaut), son contrôle de santé fonctionne, une
recherche « bucket of blood » renvoie les variantes AVI/MP4 du film avec leur taille annoncée, et « Récupérer le .torrent »
télécharge et valide le fichier — sans qu'aucune garantie de l'application ne soit affaiblie.
