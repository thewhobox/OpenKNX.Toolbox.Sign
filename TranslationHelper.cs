using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign;

public class TranslationFile
{
    [JsonPropertyName("_meta")]
    public TranslationMeta? Meta { get; set; }

    [JsonPropertyName("translations")]
    public Dictionary<string, string>? Translations { get; set; }
}

public class TranslationMeta
{
    [JsonPropertyName("module")]
    public string? Module { get; set; }

    [JsonPropertyName("source_language")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public static class TranslationHelper
{
    private static readonly HashSet<string> TranslatableElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enumeration", "Parameter", "ParameterRef", "ComObject", "ComObjectRef",
        "Channel", "ChannelIndependentBlock", "CatalogSection", "CatalogItem",
        "Product", "Message"
    };

    private static readonly HashSet<string> TranslatableAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Name", "SuffixText", "FunctionText"
    };

    /// <summary>
    /// Loads translation dictionaries from a JSON file or directory of JSON files.
    /// Each JSON file should have a "translations" object mapping source text to target text.
    /// </summary>
    public static Dictionary<string, string> LoadTranslations(string translationsPath)
    {
        var merged = new Dictionary<string, string>();
        var files = new List<string>();

        if (File.Exists(translationsPath))
        {
            files.Add(translationsPath);
        }
        else if (Directory.Exists(translationsPath))
        {
            files.AddRange(Directory.GetFiles(translationsPath, "*.json", SearchOption.AllDirectories));
        }

        foreach (string file in files)
        {
            try
            {
                string json = File.ReadAllText(file);
                var translationFile = JsonSerializer.Deserialize<TranslationFile>(json);
                if (translationFile?.Translations != null)
                {
                    foreach (var (key, value) in translationFile.Translations)
                    {
                        if (!string.IsNullOrEmpty(value))
                            merged[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: Failed to load translations from {0}: {1}", file, ex.Message);
            }
        }
        return merged;
    }

    /// <summary>
    /// Injects translation elements into the XML document's Languages block.
    /// Operates on the XDocument in memory — does not modify any file on disk.
    /// </summary>
    public static void InjectTranslations(XDocument xdoc, Dictionary<string, string> translations, string languageIdentifier = "en-US")
    {
        if (xdoc.Root == null) return;

        string ns = xdoc.Root.Name.NamespaceName;
        XName xnLanguages = XName.Get("Languages", ns);
        XName xnLanguage = XName.Get("Language", ns);
        XName xnTranslationUnit = XName.Get("TranslationUnit", ns);
        XName xnTranslationElement = XName.Get("TranslationElement", ns);
        XName xnTranslation = XName.Get("Translation", ns);

        XElement? manufacturer = xdoc.Root
            .Element(XName.Get("ManufacturerData", ns))?
            .Element(XName.Get("Manufacturer", ns));

        if (manufacturer == null)
        {
            Console.WriteLine("Warning: No Manufacturer element found, skipping translation injection.");
            return;
        }

        // Collect all translatable elements grouped by TranslationUnit RefId
        var translationUnits = new Dictionary<string, List<(string refId, string attrName, string text)>>();
        int count = 0;

        CollectTranslatableElements(xdoc.Root, translations, translationUnits, ref count);

        if (count == 0)
        {
            Console.WriteLine("No matching translations found.");
            return;
        }

        // Find or create <Languages>
        XElement? languages = manufacturer.Element(xnLanguages);
        if (languages == null)
        {
            languages = new XElement(xnLanguages);
            manufacturer.Add(languages);
        }

        // Find or create <Language Identifier="en-US">
        XElement? language = languages.Elements(xnLanguage)
            .FirstOrDefault(e => e.Attribute("Identifier")?.Value == languageIdentifier);
        if (language == null)
        {
            language = new XElement(xnLanguage, new XAttribute("Identifier", languageIdentifier));
            languages.Add(language);
        }

        // Add TranslationUnits, merging with any existing ones
        foreach (var unit in translationUnits)
        {
            XElement? existingUnit = language.Elements(xnTranslationUnit)
                .FirstOrDefault(e => e.Attribute("RefId")?.Value == unit.Key);

            XElement transUnit = existingUnit ?? new XElement(xnTranslationUnit, new XAttribute("RefId", unit.Key));
            if (existingUnit == null)
                language.Add(transUnit);

            // Track existing TranslationElement RefIds and their attributes to avoid duplicates
            var existingElements = new Dictionary<string, XElement>();
            foreach (var te in transUnit.Elements(xnTranslationElement))
            {
                string? teRefId = te.Attribute("RefId")?.Value;
                if (teRefId != null) existingElements[teRefId] = te;
            }

            foreach (var (refId, attrName, text) in unit.Value)
            {
                if (!existingElements.TryGetValue(refId, out XElement? transElem))
                {
                    transElem = new XElement(xnTranslationElement, new XAttribute("RefId", refId));
                    transUnit.Add(transElem);
                    existingElements[refId] = transElem;
                }

                // Only add if this attribute isn't already translated
                bool attrExists = transElem.Elements(xnTranslation)
                    .Any(t => t.Attribute("AttributeName")?.Value == attrName);
                if (!attrExists)
                {
                    transElem.Add(new XElement(xnTranslation,
                        new XAttribute("AttributeName", attrName),
                        new XAttribute("Text", text)));
                }
            }
        }

        Console.WriteLine("Injected {0} translations for language '{1}' ({2} translation units).",
            count, languageIdentifier, translationUnits.Count);
    }

    private static void CollectTranslatableElements(XElement element,
        Dictionary<string, string> translations,
        Dictionary<string, List<(string refId, string attrName, string text)>> translationUnits,
        ref int count)
    {
        if (TranslatableElements.Contains(element.Name.LocalName))
        {
            string? id = element.Attribute("Id")?.Value ?? element.Attribute("RefId")?.Value;
            if (!string.IsNullOrEmpty(id))
            {
                foreach (XAttribute attr in element.Attributes())
                {
                    if (TranslatableAttributes.Contains(attr.Name.LocalName) && !string.IsNullOrEmpty(attr.Value))
                    {
                        if (translations.TryGetValue(attr.Value, out string? translatedText))
                        {
                            string? unitRefId = GetTranslationUnitRefId(element);
                            if (!string.IsNullOrEmpty(unitRefId))
                            {
                                if (!translationUnits.ContainsKey(unitRefId))
                                    translationUnits[unitRefId] = new List<(string, string, string)>();
                                translationUnits[unitRefId].Add((id, attr.Name.LocalName, translatedText));
                                count++;
                            }
                        }
                    }
                }
            }
        }

        foreach (XElement child in element.Elements())
            CollectTranslatableElements(child, translations, translationUnits, ref count);
    }

    /// <summary>
    /// Walks up the XML tree to find the appropriate TranslationUnit container RefId.
    /// </summary>
    private static string? GetTranslationUnitRefId(XElement element)
    {
        XElement? current = element;
        while (current != null)
        {
            string name = current.Name.LocalName;
            if (name is "ApplicationProgram" or "Hardware" or "CatalogSection")
                return current.Attribute("Id")?.Value;

            if (name == "Catalog")
            {
                // For elements directly under Catalog, use first CatalogSection
                XElement? firstSection = current.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "CatalogSection");
                return firstSection?.Attribute("Id")?.Value;
            }

            if (name == "Product")
            {
                // Products are under Hardware — walk up to find it
                XElement? hw = current.Parent;
                while (hw != null && hw.Name.LocalName != "Hardware") hw = hw.Parent;
                return hw?.Attribute("Id")?.Value;
            }

            current = current.Parent;
        }
        return null;
    }
}
