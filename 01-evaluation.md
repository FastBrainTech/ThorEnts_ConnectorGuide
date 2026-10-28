# 1. Évaluer la source

Aucune ligne de code avant une évaluation écrite. Elle décide si la source peut être intégrée, avec quelles capacités et quelles
limites, et elle alimente directement `docs/sources.md` du dépôt principal.

## 1.1 Grille d'évaluation

| Critère | Question | Comment vérifier | Bloquant si… |
|---|---|---|---|
| Légitimité des contenus | Les œuvres sont-elles du domaine public, sous licence libre ou publiées par leurs ayants droit ? | Page « à propos », mentions de licence par œuvre, réputation, recoupement (IMDb/TMDB : année, pays, statut) | la source diffuse des œuvres protégées sans autorisation |
| Conditions d'utilisation | L'accès automatisé est-il permis ? Y a-t-il des limites de fréquence ? | CGU, `robots.txt`, page développeurs, en-têtes `Retry-After` | interdiction explicite des robots / de l'indexation |
| Mode d'accès | API JSON, flux RSS/Atom, fichiers publiés, HTML ? | Requêtes réelles avec un User-Agent explicite | accès réservé (compte, clé non délivrable), CAPTCHA, vérification de navigateur |
| Stabilité du format | Le format est-il documenté ou au moins régulier ? | Comparer plusieurs pages / réponses | format aléatoire, contenu généré en JavaScript uniquement |
| Transport | HTTPS disponible ? Redirections vers quels hôtes ? | `curl -I`, suivre les redirections | HTTPS impossible pour les fichiers `.torrent` |
| Capacités | Recherche ? pagination ? `.torrent` ? magnet ? info-hash ? seeders ? taille ? licence ? date ? | Échantillon réel | aucune (pas de `.torrent` ni magnet) |
| Disponibilité | Statut HTTP, temps de réponse, erreurs | 3 à 5 requêtes espacées | hors ligne |
| Volume | Taille d'une page, d'un index complet | `Content-Length` | réponse > limites (4 Mo recherche, 5 Mo `.torrent`) sans pagination |

**Règles de conduite pendant l'évaluation** : quelques requêtes par site au maximum, espacées, User-Agent descriptif
(ex. `ThorEnts/0.1 (documentation research)`), aucune tentative de contourner une protection, aucune récupération de contenu
vidéo. Notez la date, l'URL et le **statut HTTP observé** de chaque requête : on n'affirme que ce qu'on a observé.

## 1.2 Fiche d'évaluation — Public Domain Torrents (28/10/2026)

| | Observation |
|---|---|
| Nom | Public Domain Torrents |
| URL officielle | <https://www.publicdomaintorrents.info> (pages), <https://www.publicdomaintorrents.com> (fichiers `.torrent`) |
| Type | Site de diffusion de films classiques du domaine public (BitTorrent), pages HTML statiques |
| Contenus | Films anciens présentés comme du domaine public (catégories : horreur, western, SF, serials…). Lien IMDb par film |
| Licence par œuvre | **Aucune** mention structurée ; statut « domaine public » déclaré globalement par le site, **non vérifié** œuvre par œuvre |
| `robots.txt` | Tout autorisé (vérifié lors de l'évaluation des sources) |
| CGU | Aucune page de conditions trouvée ; page « Privacy Policy » présente |
| Accès | HTML : liste par catégorie `nshowcat.html?category={cat\|ALL}` → pages film `nshowmovie.html?movieid={n}` |
| `.torrent` | Liens absolus en **`http://`** vers `www.publicdomaintorrents.com/bt/btdownload.php?type=torrent&file={Nom}.{ext}.torrent` ; la même URL en **HTTPS** répond `200 application/x-bittorrent` |
| Variantes | Plusieurs fichiers par film (ex. « Divx 716MB AVI », « IPOD MP4 182 MB », « PSP MP4 336 MB ») |
| Seeders | Non exploitables : flux RSS `/bt/rss.php` à 0 pair et dates 1970 ; scrape du tracker en erreur de protocole |
| Année | Absente des pages observées (titre seul) |
| Protections | Aucune vérification de navigateur ni CAPTCHA observés |
| Statuts observés | `nshowcat.html?category=horror` → 200 ; `nshowmovie.html?movieid=836` → 200 `text/html; charset=UTF-8` (12 105 octets) ; `btdownload.php` (HTTPS) → 200 `application/x-bittorrent` |

## 1.3 Verdict

**Intégrable, désactivée par défaut**, avec les réserves suivantes, affichées dans l'interface :

- licence non fournie par œuvre → `Licence = "Domaine public (déclaré par la source, non vérifié)"` ;
- seeders inconnus (capacité `Seeders` non déclarée) ;
- extraction HTML : format fragile, le contrôle de santé doit détecter toute dérive de structure (statut « Suspecte ») ;
- liens `http://` réécrits en `https://` **avant** tout contrôle de domaine, jamais l'inverse.

Pourquoi « désactivée par défaut » : l'absence de licence par œuvre impose une validation humaine (Administration → activer)
après lecture des limites. C'est cohérent avec l'[ADR 0002](../ThorEnts/docs/decisions/0002-sources-autorisees.md) du dépôt principal.

## 1.4 Ce qui rendrait une source inéligible

- catalogue d'œuvres récentes ou protégées sans preuve d'autorisation (y compris les miroirs d'API d'indexeurs connus pour diffuser
  des copies non autorisées) ;
- obligation de contourner une protection d'accès ;
- conditions d'utilisation interdisant l'accès automatisé ;
- aucun moyen d'obtenir un `.torrent` ou un magnet (ex. Wikimedia Commons, download.blender.org : téléchargement direct uniquement).
