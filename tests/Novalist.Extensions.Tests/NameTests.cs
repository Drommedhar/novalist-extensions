using Novalist.Extensions.Toolkit.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// The name generator.
///
/// Seeded throughout: the generator is random by design and a test that asserts
/// on random output is a test that fails on a Tuesday. The seed makes the walk
/// repeatable without making the feature deterministic.
/// </summary>
public class NameTests
{
    private static Names Seeded(int seed = 42) => new(seed);

    private static NameRequest Ask(Action<NameRequest>? tweak = null)
    {
        var request = new NameRequest { Culture = "english", Surname = false, Count = 20 };
        tweak?.Invoke(request);
        return request;
    }

    [Fact]
    public void EveryBundledCultureProducesNames()
    {
        foreach (var culture in Names.Cultures)
        {
            var names = Seeded().Generate(Ask(r => r.Culture = culture));

            // A culture in the list that generates nothing is a culture in the
            // dropdown that does nothing when picked.
            Assert.NotEmpty(names);
        }
    }

    [Fact]
    public void ACultureNobodyBundledProducesNothing()
    {
        Assert.Empty(Seeded().Generate(Ask(r => r.Culture = "atlantean")));
        Assert.Empty(Seeded().Generate(Ask(r => r.Culture = "")));
    }

    [Fact]
    public void TheCultureNameIsNotCaseSensitive()
    {
        // It arrives from a dropdown today, and from anywhere tomorrow.
        Assert.NotEmpty(Seeded().Generate(Ask(r => r.Culture = "NORSE")));
        Assert.NotEmpty(Seeded().Generate(Ask(r => r.Culture = "  Norse  ")));
    }

    [Fact]
    public void InventedNamesAreNotJustTheListReadBack()
    {
        var invented = Seeded().Generate(Ask(r => r.Source = NameSource.Invented));
        var attested = Seeded().Generate(Ask(r => r.Source = NameSource.Attested));

        // A generator that only quotes its own list runs out, which is the whole
        // reason the chain exists. Some overlap is fine and expected - short
        // real names are exactly what the chain is most likely to rebuild.
        Assert.NotEmpty(invented);
        Assert.True(invented.Except(attested, StringComparer.OrdinalIgnoreCase).Any());
    }

    [Fact]
    public void RealNamesComeFromTheBundledList()
    {
        var names = Seeded().Generate(
            Ask(r => { r.Source = NameSource.Attested; r.Gender = "feminine"; }));

        Assert.All(names, n => Assert.Contains(n, EnglishFeminine));
    }

    private static readonly string[] EnglishFeminine =
    [
        "Alice", "Beatrice", "Clara", "Dorothy", "Edith", "Florence", "Grace",
        "Harriet", "Imogen", "Jane", "Katherine", "Lydia", "Margaret", "Nora",
        "Olive", "Prudence", "Rose", "Susanna", "Thea", "Verity", "Winifred"
    ];

    [Fact]
    public void GenderPicksTheListItDrawsFrom()
    {
        var feminine = Seeded().Generate(
            Ask(r => { r.Source = NameSource.Attested; r.Gender = "feminine"; }));
        var masculine = Seeded().Generate(
            Ask(r => { r.Source = NameSource.Attested; r.Gender = "masculine"; }));

        Assert.All(feminine, n => Assert.Contains(n, EnglishFeminine));
        Assert.All(masculine, n => Assert.DoesNotContain(n, EnglishFeminine));
    }

    [Fact]
    public void NoGenderDrawsFromBoth()
    {
        var names = Seeded().Generate(
            Ask(r => { r.Source = NameSource.Attested; r.Count = 40; }));

        Assert.Contains(names, n => EnglishFeminine.Contains(n));
        Assert.Contains(names, n => !EnglishFeminine.Contains(n));
    }

    [Theory]
    [InlineData("Ma")]
    [InlineData("th")]
    public void StartsWithIsHonoured(string prefix)
    {
        var names = Seeded().Generate(Ask(r => r.StartsWith = prefix));

        Assert.All(names, n => Assert.StartsWith(prefix, n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EndsWithIsHonoured()
    {
        var names = Seeded().Generate(Ask(r => r.EndsWith = "a"));

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.EndsWith("a", n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContainsIsHonoured()
    {
        var names = Seeded().Generate(Ask(r => r.Contains = "ar"));

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains("ar", n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LengthBoundsAreHonoured()
    {
        var names = Seeded().Generate(Ask(r => { r.MinLength = 5; r.MaxLength = 7; }));

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.InRange(n.Length, 5, 7));
    }

    [Fact]
    public void AFilterNothingSatisfiesReturnsNothingRatherThanSpinning()
    {
        // Bounded attempts, not "loop until satisfied". The writer is waiting,
        // and a short list is a better answer than a hung panel.
        Assert.Empty(Seeded().Generate(Ask(r => r.StartsWith = "Xqz")));
    }

    [Fact]
    public void NamesDoNotRepeatWithinOneSet()
    {
        var names = Seeded().Generate(Ask(r => r.Count = 20));

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ASurnameIsAddedWhenAskedFor()
    {
        var names = Seeded().Generate(Ask(r => r.Surname = true));

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains(' ', n));
    }

    [Fact]
    public void TheFilterAppliesToTheGivenNameAndNotTheSurname()
    {
        var names = Seeded().Generate(Ask(r => { r.Surname = true; r.StartsWith = "Ma"; }));

        Assert.NotEmpty(names);
        // A constraint applied to both halves would reject most of a real
        // family: the writer is looking for a given name that starts with Ma.
        Assert.All(names, n => Assert.StartsWith("Ma", n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryNameIsCapitalised()
    {
        var names = Seeded().Generate(Ask());

        Assert.All(names, n => Assert.True(char.IsUpper(n[0])));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 100)]
    public void TheCountIsClampedToSomethingSane(int asked, int most)
    {
        var names = Seeded().Generate(Ask(r => { r.Count = asked; r.MinLength = 1; }));

        Assert.InRange(names.Count, 0, most);
    }

    [Fact]
    public void AnUnseededGeneratorDoesNotRepeatItself()
    {
        // Two runs of the same request should not be the same list, or the
        // button does nothing the second time it is pressed.
        var first = new Names().Generate(Ask());
        var second = new Names().Generate(Ask());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ACultureWithNoSurnamesStillGivesNames()
    {
        // Not every world uses family names, and the request asking for one
        // must not empty the list.
        var names = Seeded().Generate(Ask(r => { r.Culture = "japanese"; r.Surname = true; }));

        Assert.NotEmpty(names);
    }
}
