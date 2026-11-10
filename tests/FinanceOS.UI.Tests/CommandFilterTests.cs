using FinanceOS.UI.Controls;
using Xunit;

namespace FinanceOS.UI.Tests;

public sealed class CommandFilterTests
{
    private static readonly CommandItem[] Items =
    [
        new() { Title = "Task nou", Group = "Task", Keywords = "create new" },
        new() { Title = "Mergi la My Day", Group = "Navigare" },
        new() { Title = "Ședință săptămânală — pregătire raport", Group = "Task" },
        new() { Title = "Reconciliere bancară BT", Group = "Task" },
        new() { Title = "Mergi la Tasks", Group = "Navigare" },
        new() { Title = "Setări", Group = "Navigare", Keywords = "settings preferinte" },
    ];

    [Theory]
    [InlineData("Ședință")]
    [InlineData("sedinta")]
    [InlineData("ŞEDINŢĂ")] // cedilla variants (ş, ţ) common in older Romanian text
    [InlineData("saptamanala")]
    public void Matches_ignore_case_and_diacritics(string query)
    {
        var results = CommandFilter.Filter(Items, query);
        Assert.Equal("Ședință săptămânală — pregătire raport", Assert.Single(results).Title);
    }

    [Fact]
    public void Empty_query_returns_everything_in_original_order()
    {
        Assert.Equal(Items, CommandFilter.Filter(Items, "   "));
        Assert.Equal(Items.Take(2), CommandFilter.Filter(Items, null, max: 2));
    }

    [Fact]
    public void Every_word_must_match()
    {
        Assert.Equal("Mergi la Tasks", Assert.Single(CommandFilter.Filter(Items, "mergi tasks")).Title);
        Assert.Empty(CommandFilter.Filter(Items, "mergi raport"));
    }

    [Fact]
    public void Title_prefix_ranks_above_word_start_above_substring()
    {
        CommandItem[] items =
        [
            new() { Title = "Raport lunar" },        // "port" inside "Raport"
            new() { Title = "Export CSV" },          // "port" inside "Export"
            new() { Title = "Portofoliu" },          // title starts with "port"
            new() { Title = "Trimite portofoliul" }, // a word starts with "port"
            new() { Title = "Facturi" },             // no match
        ];

        var titles = CommandFilter.Filter(items, "port").Select(i => i.Title).ToList();
        Assert.Equal(["Portofoliu", "Trimite portofoliul", "Raport lunar", "Export CSV"], titles);
    }

    [Fact]
    public void Keywords_and_group_match_but_rank_below_titles()
    {
        Assert.Equal("Setări", Assert.Single(CommandFilter.Filter(Items, "settings")).Title);

        var task = CommandFilter.Filter(Items, "task").Select(i => i.Title).ToList();
        Assert.Equal("Task nou", task[0]);                 // title prefix
        Assert.Equal("Mergi la Tasks", task[1]);           // word start in title
        Assert.Contains("Reconciliere bancară BT", task);  // group only
    }

    [Theory]
    [InlineData("Ședință", "sedinta")]
    [InlineData("ŢARĂ", "tara")]
    [InlineData("  Înapoi  ", "inapoi")]
    public void Normalize_strips_diacritics_and_case(string input, string expected) =>
        Assert.Equal(expected, CommandFilter.Normalize(input));
}
