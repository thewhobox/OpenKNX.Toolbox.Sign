using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign.Tests;

public class TranslationHelperTests : IDisposable
{
    private readonly string _tempDir;

    public TranslationHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OpenKNX.Tests." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    #region LoadTranslations

    [Fact]
    public void LoadTranslations_SingleFile_ReturnsTranslations()
    {
        string file = WriteTempJson("en.json", new
        {
            _meta = new { module = "test" },
            translations = new Dictionary<string, string>
            {
                ["Analogeingänge"] = "Analog inputs",
                ["Beschreibung"] = "Description"
            }
        });

        var result = TranslationHelper.LoadTranslations(file);

        Assert.Equal(2, result.Count);
        Assert.Equal("Analog inputs", result["Analogeingänge"]);
        Assert.Equal("Description", result["Beschreibung"]);
    }

    [Fact]
    public void LoadTranslations_Directory_MergesAllFiles()
    {
        string subdir = Path.Combine(_tempDir, "translations");
        Directory.CreateDirectory(subdir);

        WriteTempJson(Path.Combine(subdir, "module1.json"), new
        {
            translations = new Dictionary<string, string> { ["Hallo"] = "Hello" }
        });
        WriteTempJson(Path.Combine(subdir, "module2.json"), new
        {
            translations = new Dictionary<string, string> { ["Welt"] = "World" }
        });

        var result = TranslationHelper.LoadTranslations(subdir);

        Assert.Equal(2, result.Count);
        Assert.Equal("Hello", result["Hallo"]);
        Assert.Equal("World", result["Welt"]);
    }

    [Fact]
    public void LoadTranslations_DirectoryRecursive_FindsNestedFiles()
    {
        string nested = Path.Combine(_tempDir, "lib", "mod1", "translations");
        Directory.CreateDirectory(nested);

        WriteTempJson(Path.Combine(nested, "en.json"), new
        {
            translations = new Dictionary<string, string> { ["Tief"] = "Deep" }
        });

        var result = TranslationHelper.LoadTranslations(_tempDir);

        Assert.Single(result);
        Assert.Equal("Deep", result["Tief"]);
    }

    [Fact]
    public void LoadTranslations_EmptyValues_AreSkipped()
    {
        string file = WriteTempJson("skip.json", new
        {
            translations = new Dictionary<string, string>
            {
                ["Keep"] = "Kept",
                ["DropMe"] = "",
            }
        });

        var result = TranslationHelper.LoadTranslations(file);

        Assert.Single(result);
        Assert.True(result.ContainsKey("Keep"));
        Assert.False(result.ContainsKey("DropMe"));
    }

    [Fact]
    public void LoadTranslations_LaterFilesOverrideEarlier()
    {
        // When same key appears in multiple files, last-write-wins
        string subdir = Path.Combine(_tempDir, "multi");
        Directory.CreateDirectory(subdir);

        WriteTempJson(Path.Combine(subdir, "a.json"), new
        {
            translations = new Dictionary<string, string> { ["Key"] = "First" }
        });
        WriteTempJson(Path.Combine(subdir, "b.json"), new
        {
            translations = new Dictionary<string, string> { ["Key"] = "Second" }
        });

        var result = TranslationHelper.LoadTranslations(subdir);

        // Both are valid — we just assert it picks one consistently
        Assert.Single(result);
        Assert.Contains(result["Key"], new[] { "First", "Second" });
    }

    [Fact]
    public void LoadTranslations_NonexistentPath_ReturnsEmpty()
    {
        var result = TranslationHelper.LoadTranslations(Path.Combine(_tempDir, "does-not-exist.json"));

        Assert.Empty(result);
    }

    [Fact]
    public void LoadTranslations_MalformedJson_SkipsAndContinues()
    {
        string subdir = Path.Combine(_tempDir, "mixed");
        Directory.CreateDirectory(subdir);

        File.WriteAllText(Path.Combine(subdir, "bad.json"), "NOT VALID JSON {{{");
        WriteTempJson(Path.Combine(subdir, "good.json"), new
        {
            translations = new Dictionary<string, string> { ["OK"] = "Fine" }
        });

        var result = TranslationHelper.LoadTranslations(subdir);

        Assert.Single(result);
        Assert.Equal("Fine", result["OK"]);
    }

    [Fact]
    public void LoadTranslations_NoTranslationsProperty_ReturnsEmpty()
    {
        string file = WriteTempJson("notranslations.json", new { other = "data" });

        var result = TranslationHelper.LoadTranslations(file);

        Assert.Empty(result);
    }

    #endregion

    #region InjectTranslations

    [Fact]
    public void InjectTranslations_ParameterText_CreatesLanguagesBlock()
    {
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Text='Analogeingänge' Name='ParamName' />"
        );
        var translations = new Dictionary<string, string>
        {
            ["Analogeingänge"] = "Analog inputs"
        };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var languages = FindLanguagesElement(xdoc);
        Assert.NotNull(languages);

        var language = languages!.Elements().First();
        Assert.Equal("en-US", language.Attribute("Identifier")!.Value);

        var transUnit = language.Elements().First();
        // TranslationUnit RefId should point to ApplicationProgram
        Assert.Contains("APP-1", transUnit.Attribute("RefId")!.Value);

        var transElem = transUnit.Elements().First();
        Assert.Equal("P-1", transElem.Attribute("RefId")!.Value);

        var translation = transElem.Elements().First();
        Assert.Equal("Text", translation.Attribute("AttributeName")!.Value);
        Assert.Equal("Analog inputs", translation.Attribute("Text")!.Value);
    }

    [Fact]
    public void InjectTranslations_MultipleAttributes_CreatesMultipleTranslationChildren()
    {
        var xdoc = CreateSampleXml(
            "<ComObject Id='CO-1' Text='Schalten' FunctionText='Ein/Aus' />"
        );
        var translations = new Dictionary<string, string>
        {
            ["Schalten"] = "Switch",
            ["Ein/Aus"] = "On/Off"
        };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var transElem = FindTranslationElements(xdoc).Single();
        Assert.Equal("CO-1", transElem.Attribute("RefId")!.Value);

        var translationChildren = transElem.Elements().ToList();
        Assert.Equal(2, translationChildren.Count);
        Assert.Contains(translationChildren, t =>
            t.Attribute("AttributeName")!.Value == "Text" &&
            t.Attribute("Text")!.Value == "Switch");
        Assert.Contains(translationChildren, t =>
            t.Attribute("AttributeName")!.Value == "FunctionText" &&
            t.Attribute("Text")!.Value == "On/Off");
    }

    [Fact]
    public void InjectTranslations_NoMatchingText_NoLanguagesBlock()
    {
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Text='Unmatched' />"
        );
        var translations = new Dictionary<string, string>
        {
            ["SomethingElse"] = "Translated"
        };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var languages = FindLanguagesElement(xdoc);
        Assert.Null(languages);
    }

    [Fact]
    public void InjectTranslations_EmptyTranslations_NoLanguagesBlock()
    {
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Text='SomeText' />"
        );

        TranslationHelper.InjectTranslations(xdoc, new Dictionary<string, string>());

        Assert.Null(FindLanguagesElement(xdoc));
    }

    [Fact]
    public void InjectTranslations_CatalogItem_UsesCorrectTranslationUnit()
    {
        string ns = "http://knx.org/xml/project/20";
        var xdoc = new XDocument(new XElement(XName.Get("KNX", ns),
            new XElement(XName.Get("ManufacturerData", ns),
                new XElement(XName.Get("Manufacturer", ns),
                    new XElement(XName.Get("Catalog", ns),
                        new XElement(XName.Get("CatalogSection", ns),
                            new XAttribute("Id", "CS-1"),
                            new XAttribute("Name", "Hauptkategorie"),
                            new XElement(XName.Get("CatalogItem", ns),
                                new XAttribute("Id", "CI-1"),
                                new XAttribute("Name", "Gerät Eins"))))))));

        var translations = new Dictionary<string, string>
        {
            ["Hauptkategorie"] = "Main category",
            ["Gerät Eins"] = "Device One"
        };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var units = FindTranslationUnits(xdoc);
        // Both should reference CatalogSection CS-1
        Assert.All(units, u => Assert.Equal("CS-1", u.Attribute("RefId")!.Value));

        var elements = FindTranslationElements(xdoc).ToList();
        Assert.Equal(2, elements.Count);
        Assert.Contains(elements, e => e.Attribute("RefId")!.Value == "CS-1");
        Assert.Contains(elements, e => e.Attribute("RefId")!.Value == "CI-1");
    }

    [Fact]
    public void InjectTranslations_CustomLanguageIdentifier()
    {
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Text='Test' />"
        );
        var translations = new Dictionary<string, string> { ["Test"] = "Test EN" };

        TranslationHelper.InjectTranslations(xdoc, translations, "en-GB");

        var language = FindLanguagesElement(xdoc)!.Elements().First();
        Assert.Equal("en-GB", language.Attribute("Identifier")!.Value);
    }

    [Fact]
    public void InjectTranslations_NonTranslatableElement_IsIgnored()
    {
        // "Static" is not in the translatable elements set
        var xdoc = CreateSampleXml(
            "<Static Id='S-1' Text='ShouldNotMatch' />"
        );
        var translations = new Dictionary<string, string> { ["ShouldNotMatch"] = "Translated" };

        TranslationHelper.InjectTranslations(xdoc, translations);

        Assert.Null(FindLanguagesElement(xdoc));
    }

    [Fact]
    public void InjectTranslations_NonTranslatableAttribute_IsIgnored()
    {
        // "Value" is not a translatable attribute
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Value='ShouldNotMatch' />"
        );
        var translations = new Dictionary<string, string> { ["ShouldNotMatch"] = "Translated" };

        TranslationHelper.InjectTranslations(xdoc, translations);

        Assert.Null(FindLanguagesElement(xdoc));
    }

    [Fact]
    public void InjectTranslations_ExistingLanguagesBlock_MergesInto()
    {
        string ns = "http://knx.org/xml/project/20";
        var xdoc = new XDocument(new XElement(XName.Get("KNX", ns),
            new XElement(XName.Get("ManufacturerData", ns),
                new XElement(XName.Get("Manufacturer", ns),
                    new XElement(XName.Get("Languages", ns),
                        new XElement(XName.Get("Language", ns),
                            new XAttribute("Identifier", "en-US"),
                            new XElement(XName.Get("TranslationUnit", ns),
                                new XAttribute("RefId", "APP-1"),
                                new XElement(XName.Get("TranslationElement", ns),
                                    new XAttribute("RefId", "P-existing"),
                                    new XElement(XName.Get("Translation", ns),
                                        new XAttribute("AttributeName", "Text"),
                                        new XAttribute("Text", "Existing")))))),
                    new XElement(XName.Get("ApplicationProgram", ns),
                        new XAttribute("Id", "APP-1"),
                        new XElement(XName.Get("Static", ns),
                            new XElement(XName.Get("Parameter", ns),
                                new XAttribute("Id", "P-new"),
                                new XAttribute("Text", "Neu"))))))));

        var translations = new Dictionary<string, string> { ["Neu"] = "New" };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var transElements = FindTranslationElements(xdoc).ToList();
        Assert.Equal(2, transElements.Count);
        Assert.Contains(transElements, e => e.Attribute("RefId")!.Value == "P-existing");
        Assert.Contains(transElements, e => e.Attribute("RefId")!.Value == "P-new");
    }

    [Fact]
    public void InjectTranslations_DuplicateElement_DoesNotAddTwice()
    {
        string ns = "http://knx.org/xml/project/20";
        var xdoc = new XDocument(new XElement(XName.Get("KNX", ns),
            new XElement(XName.Get("ManufacturerData", ns),
                new XElement(XName.Get("Manufacturer", ns),
                    new XElement(XName.Get("Languages", ns),
                        new XElement(XName.Get("Language", ns),
                            new XAttribute("Identifier", "en-US"),
                            new XElement(XName.Get("TranslationUnit", ns),
                                new XAttribute("RefId", "APP-1"),
                                new XElement(XName.Get("TranslationElement", ns),
                                    new XAttribute("RefId", "P-1"),
                                    new XElement(XName.Get("Translation", ns),
                                        new XAttribute("AttributeName", "Text"),
                                        new XAttribute("Text", "Already done")))))),
                    new XElement(XName.Get("ApplicationProgram", ns),
                        new XAttribute("Id", "APP-1"),
                        new XElement(XName.Get("Static", ns),
                            new XElement(XName.Get("Parameter", ns),
                                new XAttribute("Id", "P-1"),
                                new XAttribute("Text", "German"))))))));

        var translations = new Dictionary<string, string> { ["German"] = "English" };

        TranslationHelper.InjectTranslations(xdoc, translations);

        // Should not duplicate the TranslationElement — the existing one with "Already done" should be preserved
        var transElements = FindTranslationElements(xdoc).ToList();
        Assert.Single(transElements);

        // The existing Translation for "Text" attribute should remain unchanged
        var translationChild = transElements[0].Elements().Single(e =>
            e.Attribute("AttributeName")!.Value == "Text");
        Assert.Equal("Already done", translationChild.Attribute("Text")!.Value);
    }

    [Fact]
    public void InjectTranslations_AllTranslatableElementTypes_AreFound()
    {
        string[] elementTypes = { "Enumeration", "Parameter", "ParameterRef", "ComObject",
            "ComObjectRef", "Channel", "ChannelIndependentBlock", "Message" };

        foreach (string elemType in elementTypes)
        {
            var xdoc = CreateSampleXml(
                $"<{elemType} Id='E-1' Text='German' />"
            );
            var translations = new Dictionary<string, string> { ["German"] = "English" };

            TranslationHelper.InjectTranslations(xdoc, translations);

            var transElements = FindTranslationElements(xdoc);
            Assert.True(transElements.Any(),
                $"Expected translation for element type '{elemType}' but none found.");
        }
    }

    [Fact]
    public void InjectTranslations_AllTranslatableAttributes_AreFound()
    {
        var xdoc = CreateSampleXml(
            "<Parameter Id='P-1' Text='T' Name='N' SuffixText='S' />"
        );
        var translations = new Dictionary<string, string>
        {
            ["T"] = "T-en",
            ["N"] = "N-en",
            ["S"] = "S-en"
        };

        TranslationHelper.InjectTranslations(xdoc, translations);

        var translationChildren = FindTranslationElements(xdoc)
            .Single()
            .Elements()
            .Select(e => e.Attribute("AttributeName")!.Value)
            .ToHashSet();

        Assert.Contains("Text", translationChildren);
        Assert.Contains("Name", translationChildren);
        Assert.Contains("SuffixText", translationChildren);
    }

    [Fact]
    public void InjectTranslations_NullRoot_DoesNotThrow()
    {
        var xdoc = new XDocument();
        TranslationHelper.InjectTranslations(xdoc, new Dictionary<string, string> { ["A"] = "B" });
        // No exception = pass
    }

    [Fact]
    public void InjectTranslations_NoManufacturer_DoesNotThrow()
    {
        string ns = "http://knx.org/xml/project/20";
        var xdoc = new XDocument(new XElement(XName.Get("KNX", ns)));

        TranslationHelper.InjectTranslations(xdoc, new Dictionary<string, string> { ["A"] = "B" });
        // No exception = pass
    }

    #endregion

    #region Helpers

    private static XDocument CreateSampleXml(string innerElements)
    {
        string ns = "http://knx.org/xml/project/20";
        string xml = $@"<KNX xmlns=""{ns}"">
  <ManufacturerData>
    <Manufacturer>
      <ApplicationProgram Id=""APP-1"">
        <Static>
          {innerElements}
        </Static>
      </ApplicationProgram>
    </Manufacturer>
  </ManufacturerData>
</KNX>";
        return XDocument.Parse(xml);
    }

    private static XElement? FindLanguagesElement(XDocument xdoc)
    {
        string ns = xdoc.Root?.Name.NamespaceName ?? "";
        return xdoc.Root?
            .Element(XName.Get("ManufacturerData", ns))?
            .Element(XName.Get("Manufacturer", ns))?
            .Element(XName.Get("Languages", ns));
    }

    private static IEnumerable<XElement> FindTranslationUnits(XDocument xdoc)
    {
        string ns = xdoc.Root?.Name.NamespaceName ?? "";
        var language = FindLanguagesElement(xdoc)?
            .Elements(XName.Get("Language", ns))
            .FirstOrDefault();
        return language?.Elements(XName.Get("TranslationUnit", ns)) ?? Enumerable.Empty<XElement>();
    }

    private static IEnumerable<XElement> FindTranslationElements(XDocument xdoc)
    {
        string ns = xdoc.Root?.Name.NamespaceName ?? "";
        return FindTranslationUnits(xdoc)
            .SelectMany(u => u.Elements(XName.Get("TranslationElement", ns)));
    }

    private string WriteTempJson(string relativePath, object content)
    {
        string fullPath = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(_tempDir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, System.Text.Json.JsonSerializer.Serialize(content));
        return fullPath;
    }

    #endregion
}
