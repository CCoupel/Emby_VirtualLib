using System.Collections;
using System.Reflection;
using VirtualLib.Api;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Test de round-trip exhaustif du PUT connecteur (#44, plan D3 point 2 —
/// _work/reports/planner-20260804-164559.md, "Tests Requis"). Objectif : garantir que le futur
/// helper unique <c>BuildConnectorConfig(request, existing)</c> (D3 point 1 — remplace la
/// reconstruction ad hoc de <see cref="ConnectorConfig"/> dupliquée dans <c>Post</c>/<c>Put</c> de
/// <see cref="ConfigController"/>) ne perd JAMAIS silencieusement une propriété publique lors
/// d'une mise à jour. C'est exactement le mécanisme qui a déjà produit deux bugs en production
/// (CacheEnabled jamais persisté ; LocalUserId effacé par toggleLibrary).
///
/// Écrit AVANT que dev-plugin ne livre la Phase 4 / Batch 2 du plan (aucun <c>BuildConnectorConfig</c>
/// n'existe au moment de la rédaction) — hypothèse de signature documentée ci-dessous, à réconcilier
/// au premier <c>dotnet test</c> une fois livré (consigne CDP : "normal à ce stade").
///
/// Hypothèse retenue :
///   ConnectorConfig BuildConnectorConfig(UpdateConnector request, ConnectorConfig? existing)
/// (ou toute variante avec un 1er paramètre assignable depuis <see cref="UpdateConnector"/> — par
/// exemple si <c>UpdateConnector</c> hérite de <c>CreateConnector</c> et que le helper prend ce
/// type de base). Localisé par réflexion dans TOUT l'assembly VirtualLib (nom + arité + 1er
/// paramètre compatible), pas par référence directe au type déclarant, pour tolérer un
/// emplacement et une visibilité (private/internal/static) inconnus au moment de l'écriture. Si
/// introuvable ou incompatible, le test échoue avec un message explicite plutôt que silencieusement.
///
/// Les propriétés de <see cref="ConnectorConfig"/> exposées par <see cref="UpdateConnector"/> sous
/// le MÊME nom sont copiées par réflexion (cf. <see cref="MirrorIntoUpdateRequest"/>).
/// <c>MediaFilter</c> (#44 D2) est exposé par le DTO sous 4 champs PLATS à noms différents
/// (FilterMinHeight, FilterAudioLanguages en CSV…) — mappé explicitement en plus de la copie
/// générique (cf. <see cref="MirrorMediaFilterIntoUpdateRequest"/>), pour que le round-trip
/// couvre aussi ces 4 champs.
/// </summary>
public class ConnectorConfigRoundTripTests
{
    [Fact]
    public void BuildConnectorConfig_Roundtrip_Preserves_Every_Public_Property()
    {
        var existing = NonDefaultConnectorConfig();
        var request = MirrorIntoUpdateRequest(existing);

        var method = FindBuildConnectorConfigMethod();
        var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
        var result = (ConnectorConfig)method.Invoke(target, new object?[] { request, existing })!;

        var mismatches = new List<string>();
        foreach (var prop in typeof(ConnectorConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue; // pas d'indexeur sur ConnectorConfig, garde-fou

            var expected = prop.GetValue(existing);
            var actual = prop.GetValue(result);

            if (!DeepEqual(expected, actual))
                mismatches.Add($"{prop.Name} — attendu [{Describe(expected)}], obtenu [{Describe(actual)}]");
        }

        Assert.True(mismatches.Count == 0,
            "Propriété(s) perdue(s) au round-trip BuildConnectorConfig — même famille de bug que " +
            "CacheEnabled/LocalUserId (plan #44, D3) :\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void BuildConnectorConfig_PreservesExistingPassword_WhenRequestPasswordIsEmpty()
    {
        // Comportement existant, documenté par le code actuel de Put (ConfigController.cs:379) :
        // un mot de passe vide dans la requête signifie "ne pas changer" (pattern placeholder côté
        // UI, qui n'affiche jamais le mot de passe en clair). Ce test protège explicitement ce
        // comportement contre une régression lors du refactor D3 vers BuildConnectorConfig.
        var existing = NonDefaultConnectorConfig();
        var request = MirrorIntoUpdateRequest(existing);
        request.Password = string.Empty;

        var method = FindBuildConnectorConfigMethod();
        var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
        var result = (ConnectorConfig)method.Invoke(target, new object?[] { request, existing })!;

        Assert.Equal(existing.Password, result.Password);
    }

    // -------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------

    private static ConnectorConfig NonDefaultConnectorConfig() => new()
    {
        Id = "conn-roundtrip-id",
        DisplayName = "Round Trip Connector",
        ServerType = "Plex",
        ServerUrl = "http://plex.example.test:32400",
        AuthMode = AuthMode.UserCredentials,
        ApiKey = "api-key-value",
        Username = "plex-user",
        Password = "s3cr3t-password",
        MetadataMode = MetadataMode.RemoteSyncFull,
        LibraryIds = new List<string> { "lib-a", "lib-b" },
        Enabled = false,
        KnownLibraries = new List<KnownLibrary>
        {
            new() { Id = "lib-a", Name = "Films", Type = "Movies", RemoteItemCount = 42 },
            new() { Id = "lib-b", Name = "Séries", Type = "TvShows", RemoteItemCount = 7 }
        },
        PlexMachineIdentifier = "machine-identifier-xyz",
        MaxParallelLibraries = 7,
        LocalUserId = "local-user-guid",
        LibraryOrganization = LibraryOrganization.SharedByType,
        CacheEnabled = false,
        MediaFilter = new MediaFilterConfig
        {
            MinHeight = 1080,
            AudioLanguages = new List<string> { "fra", "eng" },
            SubtitleLanguages = new List<string> { "fra" },
            IgnoreForcedSubtitles = true
        }
    };

    /// <summary>
    /// Construit une requête UpdateConnector en copiant, par réflexion et par correspondance de
    /// nom, chaque propriété de <paramref name="source"/> vers la propriété homonyme du DTO —
    /// quand elle existe. Les propriétés de ConnectorConfig qu'UpdateConnector n'expose PAS
    /// encore (CacheEnabled au moment de l'écriture, cf. D3 point 3) restent absentes de la
    /// requête : c'est précisément ce que BuildConnectorConfig doit malgré tout préserver depuis
    /// <c>existing</c>, faute de quoi le round-trip échoue.
    /// </summary>
    private static UpdateConnector MirrorIntoUpdateRequest(ConnectorConfig source)
    {
        var request = new UpdateConnector();
        foreach (var reqProp in typeof(UpdateConnector).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!reqProp.CanWrite) continue;
            var sourceProp = typeof(ConnectorConfig).GetProperty(reqProp.Name);
            if (sourceProp is null) continue;
            reqProp.SetValue(request, sourceProp.GetValue(source));
        }
        MirrorMediaFilterIntoUpdateRequest(source, request);
        return request;
    }

    /// <summary>
    /// MediaFilter (objet imbriqué sur ConnectorConfig) est exposé par le DTO sous 4 champs
    /// plats à noms différents (D2) — pas de correspondance par nom possible, mapping explicite.
    /// Reflète le mapping inverse de BuildMediaFilterConfig (ConfigController.cs) : CSV joint par
    /// virgule, sans espace, symétrique de ParseLanguageCsv (Split(',', TrimEntries)).
    /// </summary>
    private static void MirrorMediaFilterIntoUpdateRequest(ConnectorConfig source, UpdateConnector request)
    {
        var filter = source.MediaFilter;
        if (filter is null) return;

        request.FilterMinHeight = filter.MinHeight;
        request.FilterAudioLanguages = string.Join(",", filter.AudioLanguages);
        request.FilterSubtitleLanguages = string.Join(",", filter.SubtitleLanguages);
        request.FilterIgnoreForcedSubtitles = filter.IgnoreForcedSubtitles;
    }

    // -------------------------------------------------------------------------
    // Réflexion — localisation du helper et comparaison structurelle
    // -------------------------------------------------------------------------

    private static MethodInfo FindBuildConnectorConfigMethod()
    {
        // Cas attendu (D3 point 1) : helper privé statique déclaré sur ConfigController lui-même.
        // On l'interroge directement — pas besoin de charger tout l'assembly pour ce cas nominal,
        // ce qui évite FileNotFoundException sur MediaBrowser.Model (assemblies SDK Emby vendorées,
        // fournies par l'hôte réel, absentes de l'environnement de test).
        var types = new List<Type> { typeof(ConfigController) };

        // Filet de sécurité si le helper a été placé ailleurs dans l'assembly : best-effort, on
        // ignore silencieusement tout échec de chargement de type plutôt que de faire échouer le
        // test pour une raison sans rapport avec BuildConnectorConfig lui-même.
        try
        {
            types.AddRange(typeof(ConfigController).Assembly.GetTypes());
        }
        catch (ReflectionTypeLoadException ex)
        {
            types.AddRange(ex.Types.Where(t => t is not null).Select(t => t!));
        }
        catch
        {
            // Assembly partiellement chargeable dans cet environnement de test (dépendances SDK
            // Emby non vendorées) — le cas nominal ci-dessus (ConfigController direct) suffit.
        }

        var candidates = types
            .Distinct()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == "BuildConnectorConfig")
            .Where(m => m.GetParameters().Length == 2)
            .Where(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(UpdateConnector)))
            .ToList();

        Assert.True(candidates.Count > 0,
            "BuildConnectorConfig(request, existing) introuvable dans l'assembly VirtualLib — " +
            "attendu depuis D3 point 1 du plan #44 (helper unique factorisant la construction de " +
            "ConnectorConfig dans Post/Put ConfigController). Pas encore livré par dev-plugin au " +
            "moment de l'écriture de ce test (Batch 2, Phase 4), ou signature différente de " +
            "l'hypothèse retenue (1er paramètre assignable depuis UpdateConnector, 2 paramètres). " +
            "À réconcilier une fois le helper livré.");

        return candidates[0];
    }

    /// <summary>
    /// Égalité structurelle récursive : primitives/chaînes/enums par valeur, collections
    /// élément par élément, objets complexes (KnownLibrary, MediaFilterConfig…) propriété par
    /// propriété. Évite d'écrire un comparateur dédié pour chaque type imbriqué de ConnectorConfig.
    /// </summary>
    private static bool DeepEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        var type = a.GetType();
        if (type != b.GetType()) return false;

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return a.Equals(b);

        if (a is IEnumerable enumA && b is IEnumerable enumB)
        {
            var listA = enumA.Cast<object?>().ToList();
            var listB = enumB.Cast<object?>().ToList();
            if (listA.Count != listB.Count) return false;
            return listA.Zip(listB, DeepEqual).All(eq => eq);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!DeepEqual(prop.GetValue(a), prop.GetValue(b))) return false;
        }
        return true;
    }

    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Describe)) + "]",
        _ => value.ToString() ?? value.GetType().Name
    };
}
