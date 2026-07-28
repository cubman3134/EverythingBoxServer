namespace EverythingBox.Server.Tests;

/// <summary>
/// Unit tests for <see cref="AddonEndpoints.ParseSearchCore"/> — the pure function that
/// decides what search text a "/catalog/{id}/{extra}.json" request carries. This is the
/// level that can actually observe the regression these tests guard: an encoded '&amp;'
/// inside a search term must survive as part of the value, not get treated as a parameter
/// separator by whatever already decoded the route value before we saw it.
/// </summary>
public class ParseSearchTests
{
    [Fact]
    public void Encoded_ampersand_in_the_raw_target_survives_as_part_of_the_search_value()
    {
        var result = AddonEndpoints.ParseSearchCore(
            extra: "search=one",
            rawTarget: "/catalog/good:all/search=one%26two.json");

        Assert.Equal("one&two", result);
    }

    [Fact]
    public void Raw_target_with_a_query_string_is_handled()
    {
        var result = AddonEndpoints.ParseSearchCore(
            extra: "search=one",
            rawTarget: "/catalog/good:all/search=one.json?ignored=1");

        Assert.Equal("one", result);
    }

    [Fact]
    public void Null_raw_target_falls_back_to_extra()
    {
        var result = AddonEndpoints.ParseSearchCore(extra: "search=one", rawTarget: null);

        Assert.Equal("one", result);
    }

    [Fact]
    public void Empty_raw_target_falls_back_to_extra()
    {
        var result = AddonEndpoints.ParseSearchCore(extra: "search=one", rawTarget: "");

        Assert.Equal("one", result);
    }

    [Fact]
    public void Extras_segment_with_no_search_key_yields_null()
    {
        var result = AddonEndpoints.ParseSearchCore(extra: "page=2", rawTarget: null);

        Assert.Null(result);
    }

    [Fact]
    public void Search_combined_with_another_parameter_extracts_only_the_search_value()
    {
        var result = AddonEndpoints.ParseSearchCore(
            extra: "search=one&page=2",
            rawTarget: "/catalog/good:all/search=one&page=2.json");

        Assert.Equal("one", result);
    }
}
