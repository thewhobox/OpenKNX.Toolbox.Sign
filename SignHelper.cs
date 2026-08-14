using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;


namespace OpenKNX.Toolbox.Sign
{
    public class SignHelper
    {
        private static readonly bool IsWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private enum DocumentCategory
        {
            None,
            Catalog,
            Hardware,
            Application
        }
        
        public static async Task ExportKnxprodAsync(string iWorkingDir, string iKnxprodFileName, string lTempXmlFileName, string iXsdFileName, bool iIsDebug, bool iAutoXsd, string iBaggagesDirName = "", CancellationToken token = default, string translationsPath = "")
        {
            Task runner = Task.Run(() => {
                ExportKnxprod(iWorkingDir, iKnxprodFileName, lTempXmlFileName, iXsdFileName, iIsDebug, iAutoXsd, iBaggagesDirName, translationsPath);
            }, token);
            await runner;
            if(runner.Exception != null)
                throw runner.Exception;
        }

        public static void ExportKnxprod(string iWorkingDir, string iKnxprodFileName, string lTempXmlFileName, string iXsdFileName, bool iIsDebug, bool iAutoXsd, string iBaggagesDirName, string translationsPath = "")
        {
            string outputFolder = AppDomain.CurrentDomain.BaseDirectory;
            if(Directory.Exists(Path.Combine(outputFolder, "Storage")))
                outputFolder = Path.Combine(outputFolder, "Storage", "Temp");
            else if (!IsWindows)
                outputFolder = Path.Combine(Path.GetTempPath(), "OpenKNXproducer", "Temp");
            else
                outputFolder = Path.Combine(outputFolder, "Temp");

            if (Directory.Exists(outputFolder))
                Directory.Delete(outputFolder, true);

            string manuId = GetManuId(lTempXmlFileName);
            if(string.IsNullOrEmpty(manuId))
            {
                throw new Exception("Could not find ManuId in xml");
            }
            Directory.CreateDirectory(outputFolder);
            Directory.CreateDirectory(Path.Combine(outputFolder, manuId));

            // Inject translations if available (operates on a temp copy, never modifies original)
            string xmlToProcess = lTempXmlFileName;
            string? translatedTempFile = null;
            if (!string.IsNullOrEmpty(translationsPath))
            {
                var translations = TranslationHelper.LoadTranslations(translationsPath);
                if (translations.Count > 0)
                {
                    string xmlContent = File.ReadAllText(lTempXmlFileName);
                    XDocument xdoc = XDocument.Parse(xmlContent);
                    TranslationHelper.InjectTranslations(xdoc, translations);
                    translatedTempFile = Path.GetTempFileName();
                    xdoc.Save(translatedTempFile);
                    xmlToProcess = translatedTempFile;
                }
            }

            try
            {
                SplitXml(xmlToProcess, outputFolder);
            }
            finally
            {
                if (translatedTempFile != null && File.Exists(translatedTempFile))
                    File.Delete(translatedTempFile);
            }

            string iBaggageName = Path.GetFileName(iBaggagesDirName);
            string[] baggageDirs = Directory.GetDirectories(iWorkingDir, "*.baggages");
            if (string.IsNullOrEmpty(iBaggageName))
            {                
                // we derive the baggage name from iKnxprodFileName name
                if(baggageDirs.Length > 1)
                {
                    if (iKnxprodFileName.EndsWith(".knxprod"))
                    {
                        Regex regex1 = new Regex(@"([0-9]{1,2}\.[0-9]{1,2}\.[0-9]{1,2})\.knxprod");
                        if(!regex1.IsMatch(iKnxprodFileName))
                        {
                            // only if the file name does not contain version info
                            iBaggageName = Path.GetFileNameWithoutExtension(iKnxprodFileName) + ".baggages";
                        }
                    }
                }
                else if(baggageDirs.Length == 1)
                {
                    iBaggageName = Path.GetFileName(baggageDirs[0]);
                }
            }

            // Check for Baggages in case no knxprod file is given or it contains version 
            if (baggageDirs.Length > 0)
                if (string.IsNullOrEmpty(iBaggageName) || !baggageDirs.Any(d => Path.GetFileName(d).Contains(iBaggageName, StringComparison.InvariantCultureIgnoreCase)))
                    throw new Exception(string.Format("Could not determine baggage folder '{0}' to copy.", iBaggageName));

            if (!string.IsNullOrEmpty(iBaggageName))
                CopyBaggages(iWorkingDir, iBaggageName, outputFolder, manuId);

            string content = File.ReadAllText(lTempXmlFileName);
            Regex regex = new Regex("http://knx.org/xml/project/([0-9]{2})");
            Match m = regex.Match(content);
            int ns;
            if(m.Success)
                ns = int.Parse(m.Groups[1].Value);
            else
                throw new Exception("NameSpaceVersion konnte nicht ermittelt werden.");

            SignFiles(outputFolder, manuId, ns);
            CheckMaster(outputFolder, ns).Wait(); // TODO get real nsVersion
            ZipFolder(outputFolder, iKnxprodFileName);
        }

        public static void SignFiles(string outputFolder, string manuId, int ns)
        {
            string iPathETS = FindEtsPath(ns);
            if(string.IsNullOrEmpty(iPathETS))
                throw new Exception("Could not find ETS path for namespace " + ns);
            IDictionary<string, string> applProgIdMappings = new Dictionary<string, string>();
            IDictionary<string, string> applProgHashes = new Dictionary<string, string>();
            IDictionary<string, string> mapBaggageIdToFileIntegrity = new Dictionary<string, string>(50);

            FileInfo hwFileInfo = new FileInfo(Path.Combine(outputFolder, manuId, "Hardware.xml"));
            FileInfo catalogFileInfo = new FileInfo(Path.Combine(outputFolder, manuId, "Catalog.xml"));

            string appFile = "";
            foreach(string file in Directory.GetFiles(Path.Combine(outputFolder, manuId)))
            {
                if(file.Contains("_A-"))
                {
                    appFile = file;
                    break;
                }
            }
            FileInfo appInfo = new FileInfo(appFile);

            ApplicationProgramHasher aph = new ApplicationProgramHasher(appInfo, mapBaggageIdToFileIntegrity, iPathETS, ns, true);
            aph.HashFile();

            applProgIdMappings.Add(aph.OldApplProgId, aph.NewApplProgId);
            if (!applProgHashes.ContainsKey(aph.NewApplProgId))
                applProgHashes.Add(aph.NewApplProgId, aph.GeneratedHashString);

            HardwareSigner hws = new HardwareSigner(hwFileInfo, applProgIdMappings, applProgHashes, iPathETS, ns, true);
            hws.SignFile();
            IDictionary<string, string> hardware2ProgramIdMapping = hws.OldNewIdMappings;

            CatalogIdPatcher cip = new CatalogIdPatcher(catalogFileInfo, hardware2ProgramIdMapping, iPathETS, ns);
            cip.Patch();

            var manuDir = Path.Combine(outputFolder, manuId);

            // macOS/Linux: make the files match the Windows layout before signing.
            if (!IsWindows)
            {
                NormalizeXmlForSigning(manuDir);      // CRLF + UTF-8 BOM
                FlattenBaggagesToBackslash(manuDir);  // "\" relative paths
            }

            XmlSigning.SignDirectory(manuDir, iPathETS);

            // restore real Baggages subdirectories after signing (keeps signature valid)
            if (!IsWindows)
                RestoreBaggagesFromBackslash(manuDir);
        }

        private static void FlattenBaggagesToBackslash(string manuDir)
        {
            string bagDir = Path.Combine(manuDir, "Baggages");
            if (!Directory.Exists(bagDir)) return;

            // "Baggages/AD/00/03/ets.png" -> flat file "Baggages\AD\00\03\ets.png"
            foreach (string file in Directory.GetFiles(bagDir, "*", SearchOption.AllDirectories))
            {
                string flatName = Path.GetRelativePath(manuDir, file).Replace(Path.DirectorySeparatorChar, '\\');
                File.Move(file, Path.Combine(manuDir, flatName), overwrite: true);
            }
            Directory.Delete(bagDir, true);
        }

        // "Baggages\AD\00\03\ets.png" flat files -> real Baggages/AD/00/03/ subdirectories
        private static void RestoreBaggagesFromBackslash(string manuDir)
        {
            foreach (string file in Directory.GetFiles(manuDir))
            {
                string name = Path.GetFileName(file);
                if (!name.Contains('\\')) continue;
                string dest = Path.Combine(manuDir, name.Replace('\\', Path.DirectorySeparatorChar));
                _ = Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, overwrite: true);
            }
        }

        private static void NormalizeXmlForSigning(string folder)
        {
            foreach (string file in Directory.GetFiles(folder, "*.xml"))
            {
                string text = File.ReadAllText(file);
                text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");  // -> CRLF
                File.WriteAllText(file, text, new System.Text.UTF8Encoding(true));
            }
        }

        public static void ZipFolder(string outputFolder, string outputFile)
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(outputFolder, outputFile);
        }

        public static async Task CheckMaster(string outputFolder, int ns)
        {
            string basePath = Path.GetFullPath("..", outputFolder);
            if(File.Exists(Path.Combine(outputFolder, "knx_master.xml")))
            {
                string content = File.ReadAllText(Path.Combine(outputFolder, "knx_master.xml"));
                if(content.Contains($"http://knx.org/xml/project/{ns}"))
                {
                    //save it
                    if (!File.Exists(Path.Combine(basePath, "Masters", $"project-{ns}.xml")))
                    {
                        if(!Directory.Exists(Path.Combine(basePath, "Masters")))
                            Directory.CreateDirectory(Path.Combine(basePath, "Masters"));
                        File.Copy(Path.Combine(outputFolder, "knx_master.xml"), Path.Combine(basePath, "Masters", $"project-{ns}.xml"));
                    }
                    return;
                }
                File.Delete(Path.Combine(outputFolder, "knx_master.xml"));
            }

            if (!File.Exists(Path.Combine(basePath, "Masters", $"project-{ns}.xml")))
            {
                if (!Directory.Exists(Path.Combine(basePath, "Masters")))
                    Directory.CreateDirectory(Path.Combine(basePath, "Masters"));
                HttpClient client = new HttpClient();
                string masterXML = await client.GetStringAsync($"https://update.knx.org/data/XML/project-{ns}/knx_master.xml");
                File.WriteAllText(Path.Combine(basePath, "Masters", $"project-{ns}.xml"), masterXML);
            }
            File.Copy(Path.Combine(basePath, "Masters", $"project-{ns}.xml"), Path.Combine(outputFolder, $"knx_master.xml"));
        }

        private static void SplitXml(string lTempXmlFileName, string outputFolder)
        {
            //if (ValidateXsd(iWorkingDir, lTempXmlFileName, lTempXmlFileName, iXsdFileName, iAutoXsd)) return 1;

            XDocument? xdoc = null;
            string xmlContent = File.ReadAllText(lTempXmlFileName);
            xdoc = XDocument.Parse(xmlContent, LoadOptions.SetLineInfo);

            XNode? lXmlModel = xdoc.FirstNode;
            if (lXmlModel != null && lXmlModel.NodeType == XmlNodeType.ProcessingInstruction)
                lXmlModel.Remove();

            if(xdoc.Root == null)
                throw new Exception("Root element not found in xml");
            string ns = xdoc.Root.Name.NamespaceName;
            XElement? xmanu = xdoc.Root.Element(XName.Get("ManufacturerData", ns))?.Element(XName.Get("Manufacturer", ns));

            if(xmanu == null)
                throw new Exception("Manufacturer element not found in xml");

            string? manuId = xmanu.Attribute("RefId")?.Value;
            XElement? xcata = xmanu.Element(XName.Get("Catalog", ns));
            XElement? xhard = xmanu.Element(XName.Get("Hardware", ns));
            XElement? xappl = xmanu.Element(XName.Get("ApplicationPrograms", ns));
            XElement? xbagg = xmanu.Element(XName.Get("Baggages", ns));

            if(string.IsNullOrEmpty(manuId))
                throw new Exception("Manufacturer Id not found in xml");
            if(xcata == null)
                throw new Exception("Catalog element not found in xml");
            if(xhard == null)
                throw new Exception("Hardware element not found in xml");
            if(xappl == null)
                throw new Exception("ApplicationPrograms element not found in xml");

            List<XElement> xcataL = new List<XElement>();
            List<XElement> xhardL = new List<XElement>();
            List<XElement> xapplL = new List<XElement>();
            List<XElement> xbaggL = new List<XElement>();
            XElement? xlangs = xmanu.Element(XName.Get("Languages", ns));

            if (xlangs != null)
            {
                xlangs.Remove();
                foreach (XElement xTrans in xlangs.Descendants(XName.Get("TranslationUnit", ns)).ToList())
                {
                    DocumentCategory lCategory = GetDocumentCategory(xTrans);
                    switch (lCategory)
                    {
                        case DocumentCategory.Catalog:
                            AddTranslationUnit(xTrans, xcataL, ns);
                            break;
                        case DocumentCategory.Hardware:
                            AddTranslationUnit(xTrans, xhardL, ns);
                            break;
                        case DocumentCategory.Application:
                            AddTranslationUnit(xTrans, xapplL, ns);
                            break;
                        default:
                            throw new Exception("Unknown Translation Type: " + lCategory.ToString());
                    }

                }
            }
            xhard.Remove();
            if (xbagg != null) xbagg.Remove();

            //Save Catalog
            xappl.Remove();
            if (xcataL.Count > 0 && xlangs != null)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xcataL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            xdoc.Save(Path.Combine(outputFolder, manuId, "Catalog.xml"));
            if (xcataL.Count > 0 && xlangs != null) xlangs.Remove();
            xcata.Remove();

            // Save Hardware
            xmanu.Add(xhard);
            if (xhardL.Count > 0 && xlangs != null)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xhardL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            xdoc.Save(Path.Combine(outputFolder, manuId, "Hardware.xml"));
            if (xhardL.Count > 0 && xlangs != null) xlangs.Remove();
            xhard.Remove();

            if (xbagg != null)
            {
                // Save Baggages
                xmanu.Add(xbagg);
                if (xbaggL.Count > 0 && xlangs != null)
                {
                    xlangs.Elements().Remove();
                    foreach (XElement xlang in xbaggL)
                        xlangs.Add(xlang);
                    xmanu.Add(xlangs);
                }
                xdoc.Save(Path.Combine(outputFolder, manuId, "Baggages.xml"));
                if (xbaggL.Count > 0 && xlangs != null) xlangs.Remove();
                xbagg.Remove();
            }

            xmanu.Add(xappl);
            if (xapplL.Count > 0 && xlangs != null)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xapplL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            string? appId = xappl.Elements(XName.Get("ApplicationProgram", ns)).First().Attribute("Id")?.Value;
            if(string.IsNullOrEmpty(appId))
                throw new Exception("ApplicationProgram Id not found in xml");
            xdoc.Save(Path.Combine(outputFolder, manuId, $"{appId}.xml"));
            if (xapplL.Count > 0 && xlangs != null) xlangs.Remove();
        }

        private static void CopyBaggages(string iWorkingDir, string iBaggageName, string outputFolder, string manuId)
        {
            string lSourceBaggageName = Path.Combine(iWorkingDir, iBaggageName);
            var lSourceBaggageDir = new DirectoryInfo(lSourceBaggageName);
            Directory.CreateDirectory(Path.Combine(outputFolder, manuId, "Baggages"));
            if (lSourceBaggageDir.Exists)
                lSourceBaggageDir.DeepCopy(Path.Combine(outputFolder, manuId, "Baggages"));
        }

        private static DocumentCategory GetDocumentCategory(XElement iTranslationUnit)
        {
            DocumentCategory lCategory = DocumentCategory.None;
            string? lId = iTranslationUnit.Attribute("RefId")?.Value;

            if(string.IsNullOrEmpty(lId))
                throw new Exception("RefId not found in TranslationUnit");

            lId = lId.Substring(6);
            if (lId.StartsWith("_A-"))
                lCategory = DocumentCategory.Application;
            else if (lId.StartsWith("_CS-"))
                lCategory = DocumentCategory.Catalog;
            else if (lId.StartsWith("_H-") && lId.Contains("_CI-"))
                lCategory = DocumentCategory.Catalog;
            else if (lId.StartsWith("_H-") && lId.Contains("_P-"))
                lCategory = DocumentCategory.Hardware;
            else if (lId.StartsWith("_H-"))
                lCategory = DocumentCategory.Hardware;

            return lCategory;
        }
        
        private static string GetManuId(string lTempXmlFileName)
        {
            string content = File.ReadAllText(lTempXmlFileName);
            Regex regex = new Regex("<Manufacturer RefId=\"(M-[0-9A-F]{4})\">");
            Match m = regex.Match(content);
            if(m.Success)
                return m.Groups[1].Value;
            return "";
        }

        private static void AddTranslationUnit(XElement iTranslationUnit, List<XElement> iLanguageList, string iNamespaceName)
        {
            // we assume, that here are adding just few TranslationUnits
            // get parent element (Language)
            XElement? lSourceLanguage = iTranslationUnit.Parent;
            if(lSourceLanguage == null)
                throw new Exception("Language element not found in xml");
            string? lSourceLanguageId = lSourceLanguage.Attribute("Identifier")?.Value;
            if(string.IsNullOrEmpty(lSourceLanguageId))
                throw new Exception("Language Identifier not found in xml");
            XElement? lTargetLanguage = iLanguageList.FirstOrDefault(lang => lang.Attribute("Identifier")?.Value == lSourceLanguageId);
            if (lTargetLanguage == null)
            {
                // we create language element
                lTargetLanguage = new XElement(XName.Get("Language", iNamespaceName), new XAttribute("Identifier", lSourceLanguageId));
                iLanguageList.Add(lTargetLanguage);
            }
            iTranslationUnit.Remove();
            lTargetLanguage.Add(iTranslationUnit);
        }
    
        //installation path of a valid ETS instance (only ETS5 or ETS6 supported)
        private static List<string> gPathETS = new List<string> {
            @"C:\Program Files (x86)\ETS6",
            @"C:\Program Files\ETS6",
            @"C:\Program Files (x86)\ETS5",
            @"C:\Program Files\ETS5"
        };

        private static List<EtsVersion> etsVersions = new() {
            new EtsVersion("6.3", 24, false),
            new EtsVersion("6.2", 24, false),
            new EtsVersion("6.1", 23, false),
            new EtsVersion("6.0", 22, false),
            new EtsVersion("5.7", 20, true),
            new EtsVersion("5.6", 14, true),
            new EtsVersion("5.1", 13, true),
            new EtsVersion("5.0", 12, true),
            new EtsVersion("4.0", 11, true),
        };

#nullable enable
        private static EtsVersion? checkEtsPath(string path, int ns)
        {
            if(!File.Exists(System.IO.Path.Combine(path, "Knx.Ets.Xml.ObjectModel.dll"))) return null;
                string versionInfo = FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(path, "Knx.Ets.Xml.ObjectModel.dll")).FileVersion?.Substring(0,3) ?? "0.0";
            
            EtsVersion? vers = etsVersions.FirstOrDefault(v => v.Version == versionInfo);

            if(vers == null) vers = etsVersions.First();
            if(vers.CheckNs(ns)) return vers;
            return null;
        }

        public static string FindEtsPath(int namespaceVersion)
        {
            string binPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "CV");
            if (Directory.Exists(binPath)) {
                foreach(string path in Directory.GetDirectories(binPath).Reverse()) {
                    EtsVersion? ets = checkEtsPath(path, namespaceVersion);
                    if(ets != null) {
                        return path;
                    }
                }
            }

            foreach(string path in gPathETS)
            {
                EtsVersion? ets = checkEtsPath(path, namespaceVersion);
                if(ets != null) {
                    return path;
                }
            }

            return "";
        }
    }
}
#nullable disable