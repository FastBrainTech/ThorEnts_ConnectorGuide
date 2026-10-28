# Annexe A — Référence du contrat

Source : `src/TorrentSearch.Application/Connectors/ITorrentSourceConnector.cs` et `src/TorrentSearch.Infrastructure/Connectors/ConnectorBase.cs`
(état au 28/10/2026).

## A.1 Interface

| Membre | Rôle | Appelé par |
|---|---|---|
| `string ConnectorType` | identifiant stable, référencé par `Source.ConnectorType` | `ConnectorRegistry` |
| `ConnectorDescriptor Descriptor` | nom, `SourceCapabilities`, limites (affichées dans l'administration) | semis, administration |
| `ValidateConfiguration(SourceContext)` | vérifications sans réseau (URL, hôtes…) ; liste de problèmes | contrôle de santé |
| `SearchAsync(SourceContext, ConnectorSearchRequest, CancellationToken)` | recherche → `ConnectorSearchResult` | `SearchService`, `CatalogMaintenanceService` (via `SourceExecutor`) |
| `CheckHealthAsync(SourceContext, CancellationToken)` | contrôle de disponibilité et de format | `SourceHealthService` (planifié et manuel) |
| `SyncBatchAsync(SourceContext, string? cursor, int batchSize, CancellationToken)` | lot de synchronisation idempotent (facultatif) | `CatalogMaintenanceService` |

Avec `ConnectorBase`, on implémente seulement `ConnectorType`, `Descriptor`, `SearchCoreAsync`, `ProbeAsync` (et éventuellement
`ValidateConfiguration`, `SyncBatchAsync`).

## A.2 Types

| Type | Champs |
|---|---|
| `SourceContext` | `Id`, `Key`, `Name`, `BaseUrl`, `AllowedHosts`, `Timeout` (1–120 s), `MaxRequestsPerMinute`, `MaxResults` (1–200) |
| `ConnectorSearchRequest` | `Text`, `Year?`, `AlternativeTitles` (titres TMDB quand recherche par identifiant), `Page` |
| `ReleaseCandidate` | `ExternalId`*, `ReleaseName`*, `WorkTitle`, `Year`, `SizeBytes`, `Seeders`, `Leechers`, `AvailabilityCollectedAt`, `PublishedAt`, `DetailsUrl`, `TorrentUrl`, `MagnetUri`, `InfoHash`, `Languages`, `Subtitles`, `Licence`, `Quality`, `Resolution`, `Codec` (* obligatoires) |
| `ConnectorSearchResult` | `ErrorCode`, `Message`, `Releases`, `Warnings`, `HasMore` ; fabriques `Ok(...)`, `Fail(code, message)` |
| `ConnectorHealthResult` | `Status`, `ErrorCode`, `HttpStatus`, `Message`, `Warnings`, `RetryAfter` |
| `ConnectorSyncBatch` | `ErrorCode`, `Message`, `Releases`, `NextCursor`, `Completed` |
| `SourceHttpResponse` | `StatusCode`, `Content` (octets bornés), `ContentType`, `FinalUri`, `Warnings` |

## A.3 Ce que l'application fait après le connecteur (à ne pas refaire)

| Étape | Composant |
|---|---|
| Validation des URL (`http(s)` absolues, sans identifiants), `TorrentUrl` limitée aux hôtes autorisés | `CatalogWriter.Apply` |
| Validation de l'info-hash (hex 40 ou base32 32) et du magnet ; incohérence hash/magnet → les deux écartés | `CatalogWriter.Apply` |
| Complément par analyse du nom de release (année, résolution, qualité, codec, langue) si le connecteur ne les fournit pas | `ReleaseNameParser` |
| Horodatage des seeders (`AvailabilityCollectedAt`) | `CatalogWriter.Apply` |
| Upsert idempotent par (source, `ExternalId`) | `CatalogWriter.UpsertReleasesAsync` |
| Rattachement prudent à un film (TMDB ou local) | `CatalogWriter.ResolveMovieAsync` |
| Regroupement par info-hash, doublons probables, classement explicable, filtres | `ResultView`, `ResultRanker` |
| Cache par requête et par source, catalogue local | `SearchService` |
| Récupération et validation du `.torrent`, calcul du hash, magnet | `TorrentRetrievalService` |

## A.4 `SourceCapabilities`

`Search`, `Pagination`, `TorrentFile`, `Magnet`, `Seeders`, `InfoHash`, `Licence`, `Size`, `PublicationDate`, `CatalogSync`.
Elles sont recopiées dans la ligne `sources` au démarrage (semis) et affichées dans l'administration. `TorrentFile` est exigée par
`TorrentRetrievalService` pour autoriser une récupération ; `CatalogSync` pour autoriser la synchronisation.

## A.5 `SourceErrorCode`

| Code | Quand le renvoyer |
|---|---|
| `SourceUnavailable` | réseau, DNS, HTTP 5xx, 404/410 |
| `Timeout` | délai dépassé (géré par `SourceExecutor`) |
| `AccessDenied` | HTTP 401/403 (ne jamais tenter de contourner) |
| `RateLimited` | HTTP 429 ou quota local |
| `MalformedResponse` | format inattendu (JSON/XML/HTML illisible, structure changée) |
| `NotSupported` | opération ou configuration non prise en charge |
| `InvalidResult` | contenu reçu non conforme (ex. fichier qui n'est pas un `.torrent`) |
| `DownloadFailed` | échec générique de récupération de fichier |
| `SuspiciousRedirect` | redirection hors domaine ou vers HTTP |
| `TlsError` | certificat invalide |
| `ForbiddenDestination` | URL hors hôtes autorisés ou destination locale/privée (SSRF) |
| `ResponseTooLarge` | réponse au-delà de la limite |
| `Disabled` | source désactivée (manuellement ou automatiquement) |
| `InternalError` | bogue du connecteur (exception non prévue, capturée par `SourceExecutor`) |

## A.6 Exceptions converties par `ConnectorBase`

| Exception levée dans `SearchCoreAsync` | Résultat |
|---|---|
| `SourceRequestException` (levée par `SourceHttpClient`) | `Fail(ex.Code, ex.Message)` |
| `JsonException`, `XmlException`, `FormatException`, `InvalidOperationException`, `KeyNotFoundException` | `Fail(MalformedResponse)` |
| `OperationCanceledException` (délai) | `Timeout` (dans `SourceExecutor`) |
| autre | `InternalError` (dans `SourceExecutor`, journalisée) |
