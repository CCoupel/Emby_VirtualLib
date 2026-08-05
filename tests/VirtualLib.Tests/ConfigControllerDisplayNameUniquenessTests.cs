using System.Reflection;
using VirtualLib;
using VirtualLib.Api;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests unitaires de l'unicité de <c>ConnectorConfig.DisplayName</c> (#44). Comble le [MAJEUR]
/// signalé par code-reviewer (_work/reports/code-reviewer-20260805-094419.md, "Race
/// inter-connecteurs sur un dossier de bibliothèque partagé par collision de nom") : sans cette
/// validation, deux connecteurs au même nom assaini écrivent dans le même dossier physique, et le
/// nettoyage d'orphelins de l'un peut classer les fichiers fraîchement écrits par l'autre comme
/// orphelins — un vrai risque de perte de données une fois #12 active la suppression réelle.
///
/// Correctif livré par dev-plugin dans la même session (<c>ConfigController.cs:926</c>) :
/// <c>private static void EnsureUniqueDisplayName(PluginConfiguration config, string displayName,
/// string? excludeId)</c> — lève <c>ArgumentException</c> sur collision, comparaison sur le nom
/// ASSAINI (<c>StrmGenerator.SanitizeName</c>, le nom de dossier réel) et insensible à la casse,
/// appelée par <c>Post</c> (excludeId: null) et <c>Put</c> (excludeId: l'Id du connecteur en cours
/// de modification, pour ne pas se comparer à lui-même).
///
/// Invoquée par réflexion (méthode privée statique — même approche que
/// <c>ConnectorConfigRoundTripTests</c> pour <c>BuildConnectorConfig</c> en Batch 2), résiliente à
/// un changement de signature/visibilité.
/// </summary>
public class ConfigControllerDisplayNameUniquenessTests
{
    [Fact]
    public void EnsureUniqueDisplayName_ExactDuplicate_Throws()
    {
        var config = ConfigWithConnectors(("id-1", "Emby-B"));

        var ex = InvokeAndCaptureException(config, "Emby-B", excludeId: null);

        Assert.NotNull(ex);
    }

    [Fact]
    public void EnsureUniqueDisplayName_CaseInsensitiveDuplicate_Throws()
    {
        // Collision sur un système de fichiers insensible à la casse (Windows, macOS par défaut) —
        // documenté explicitement dans le commentaire du correctif.
        var config = ConfigWithConnectors(("id-1", "Emby-B"));

        var ex = InvokeAndCaptureException(config, "EMBY-B", excludeId: null);

        Assert.NotNull(ex);
    }

    [Fact]
    public void EnsureUniqueDisplayName_SameSanitizedFolderName_DifferentRawName_Throws()
    {
        // Le vrai risque n'est pas la collision de chaîne brute mais la collision du nom de DOSSIER
        // réel — deux libellés différents qui s'assainissent (StrmGenerator.SanitizeName) vers le
        // même résultat doivent collisionner tout autant qu'un doublon exact. SanitizeName
        // remplace '/' et '\' (tous deux invalides pour un nom de fichier) par '_' : deux noms
        // distincts convergent donc vers le même dossier "Emby_B".
        var config = ConfigWithConnectors(("id-1", "Emby/B"));

        var ex = InvokeAndCaptureException(config, "Emby\\B", excludeId: null);

        Assert.NotNull(ex);
    }

    [Fact]
    public void EnsureUniqueDisplayName_NoCollision_DoesNotThrow()
    {
        var config = ConfigWithConnectors(("id-1", "Emby-B"), ("id-2", "Plex-Main"));

        var ex = InvokeAndCaptureException(config, "Jellyfin-Home", excludeId: null);

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureUniqueDisplayName_Update_KeepingOwnUnchangedName_DoesNotThrow()
    {
        // Cas Put le plus fréquent : l'utilisateur modifie un champ sans toucher au nom — le
        // connecteur ne doit jamais collisionner avec lui-même (excludeId).
        var config = ConfigWithConnectors(("id-1", "Emby-B"));

        var ex = InvokeAndCaptureException(config, "Emby-B", excludeId: "id-1");

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureUniqueDisplayName_Update_RenamingToAnotherConnectorsName_Throws()
    {
        var config = ConfigWithConnectors(("id-1", "Emby-B"), ("id-2", "Plex-Main"));

        var ex = InvokeAndCaptureException(config, "Plex-Main", excludeId: "id-1");

        Assert.NotNull(ex);
    }

    // -------------------------------------------------------------------------

    private static PluginConfiguration ConfigWithConnectors(params (string Id, string DisplayName)[] connectors)
    {
        var config = new PluginConfiguration();
        foreach (var (id, displayName) in connectors)
            config.Connectors.Add(new ConnectorConfig { Id = id, DisplayName = displayName });
        return config;
    }

    private static Exception? InvokeAndCaptureException(PluginConfiguration config, string displayName, string? excludeId)
    {
        var method = FindEnsureUniqueDisplayNameMethod();
        try
        {
            method.Invoke(null, new object?[] { config, displayName, excludeId });
            return null;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // MethodInfo.Invoke enveloppe toute exception levée par la méthode cible.
            return tie.InnerException;
        }
    }

    private static MethodInfo FindEnsureUniqueDisplayNameMethod()
    {
        var method = typeof(ConfigController).GetMethod(
            "EnsureUniqueDisplayName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(method is not null,
            "ConfigController.EnsureUniqueDisplayName(config, displayName, excludeId) introuvable — " +
            "attendu depuis le correctif [MAJEUR] code-reviewer sur la collision de DisplayName " +
            "entre connecteurs (#44). Pas encore livré par dev-plugin au moment de l'écriture de ce " +
            "test, ou signature différente de l'hypothèse retenue.");

        return method!;
    }
}
