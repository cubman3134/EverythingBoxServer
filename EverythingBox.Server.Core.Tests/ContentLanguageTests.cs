using System;
using System.Collections.Generic;
using EverythingBox.Server.Abstractions;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class ContentLanguageTests
{
    private static Dictionary<string, string> H(string acceptLanguage) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Accept-Language"] = acceptLanguage };

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData("EN", "English")]
    [InlineData("en-US,en;q=0.9", "English")]   // list + region + q-weight
    [InlineData("pt-BR", "Portuguese")]
    public void MapsAcceptLanguageToReleaseName(string header, string expected)
        => Assert.Equal(expected, ContentLanguage.FromHeaders(H(header)));

    [Fact] public void NullHeadersIsNull() => Assert.Null(ContentLanguage.FromHeaders(null));
    [Fact] public void MissingHeaderIsNull() => Assert.Null(ContentLanguage.FromHeaders(new Dictionary<string, string>()));
    [Fact] public void BlankIsNull() => Assert.Null(ContentLanguage.FromHeaders(H("  ")));
    [Fact] public void UnmappedCodeIsNull() => Assert.Null(ContentLanguage.FromHeaders(H("xx")));
}
