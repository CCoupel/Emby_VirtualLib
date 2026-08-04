using System.Collections.Generic;
using System.Linq;
using VirtualLib.Core.Filtering;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests unitaires de <see cref="LanguageMatcher"/> (#44) — écrits depuis
/// contracts/models.md ("Règle de normalisation LanguageCode") et la tâche 4 du plan
/// (_work/reports/planner-20260804-164559.md) : équivalences ISO 639-1/639-2B/639-2T et
/// libellés anglais/français convergent vers un code canonique 3 lettres minuscules ;
/// insensibilité à la casse ; code inconnu conservé tel quel en minuscules sans exception ;
/// entrée nulle/vide.
/// </summary>
public class LanguageMatcherTests
{
    // ---- CA4 — équivalences fr/fre/fra/french/français convergent explicitement vers "fra" ----

    [Theory]
    [InlineData("fr")]
    [InlineData("fre")]
    [InlineData("fra")]
    [InlineData("french")]
    [InlineData("French")]
    [InlineData("FRENCH")]
    [InlineData("Français")]
    [InlineData("français")]
    [InlineData("  fr  ")] // espaces superflus tolérés (Trim)
    public void Normalize_French_Variants_Converge_To_Fra(string input)
    {
        Assert.Equal("fra", LanguageMatcher.Normalize(input));
    }

    // ---- Autres langues de la table minimale du plan : toutes les variantes d'une même langue
    // doivent converger vers le MÊME code canonique. On ne fige pas la valeur exacte (639-2/B vs
    // /T) pour ces langues secondaires — seule la convergence est un critère du contrat. ----

    public static IEnumerable<object[]> LanguageVariantGroups()
    {
        yield return new object[] { new[] { "en", "eng", "english", "English", "ENGLISH" } };
        yield return new object[] { new[] { "es", "spa", "spanish", "Spanish" } };
        yield return new object[] { new[] { "de", "ger", "deu", "german", "German" } };
        yield return new object[] { new[] { "it", "ita", "italian", "Italian" } };
        yield return new object[] { new[] { "pt", "por", "portuguese", "Portuguese" } };
        yield return new object[] { new[] { "nl", "dut", "nld", "dutch", "Dutch" } };
        yield return new object[] { new[] { "ja", "jpn", "japanese", "Japanese" } };
        yield return new object[] { new[] { "ko", "kor", "korean", "Korean" } };
        yield return new object[] { new[] { "zh", "chi", "zho", "chinese", "Chinese" } };
        yield return new object[] { new[] { "ru", "rus", "russian", "Russian" } };
        yield return new object[] { new[] { "ar", "ara", "arabic", "Arabic" } };
    }

    [Theory]
    [MemberData(nameof(LanguageVariantGroups))]
    public void Normalize_Known_Language_Variants_All_Converge_To_Same_Canonical_Code(string[] variants)
    {
        var normalized = variants.Select(LanguageMatcher.Normalize).Distinct().ToList();

        Assert.Single(normalized); // toutes les variantes du groupe donnent le même résultat
        Assert.Matches("^[a-z]{3}$", normalized[0]); // code canonique 3 lettres minuscules
    }

    // ---- Insensibilité à la casse (cas générique, au-delà du groupe français ci-dessus) ----

    [Fact]
    public void Normalize_Is_Case_Insensitive()
    {
        Assert.Equal(LanguageMatcher.Normalize("ENG"), LanguageMatcher.Normalize("eng"));
        Assert.Equal(LanguageMatcher.Normalize("Spanish"), LanguageMatcher.Normalize("SPANISH"));
    }

    // ---- Code inconnu : conservé tel quel en minuscules, jamais d'exception ----

    [Theory]
    [InlineData("xyz", "xyz")]
    [InlineData("Klingon", "klingon")]
    [InlineData("XX", "xx")]
    [InlineData("Und", "und")] // code ISO "undetermined" — non mappé dans la table, doit passer tel quel
    public void Normalize_Unknown_Code_Is_Lowercased_Without_Throwing(string input, string expected)
    {
        var exception = Record.Exception(() => LanguageMatcher.Normalize(input));

        Assert.Null(exception);
        Assert.Equal(expected, LanguageMatcher.Normalize(input));
    }

    // ---- Entrée nulle / vide ----

    [Fact]
    public void Normalize_Null_Does_Not_Throw_And_Returns_Null_Or_Empty()
    {
        var exception = Record.Exception(() => LanguageMatcher.Normalize(null));

        Assert.Null(exception);
        Assert.True(string.IsNullOrEmpty(LanguageMatcher.Normalize(null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_Empty_Or_Whitespace_Does_Not_Throw_And_Returns_Null_Or_Empty(string input)
    {
        var exception = Record.Exception(() => LanguageMatcher.Normalize(input));

        Assert.Null(exception);
        Assert.True(string.IsNullOrEmpty(LanguageMatcher.Normalize(input)));
    }
}
