using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Object = System.Object;

namespace Feeder
{
    public class FeederGoogleSheetController
    {
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        private readonly List<string> sheetNames = new List<string>();
        private readonly Dictionary<string, List<List<string>>> sheetValues = new Dictionary<string, List<List<string>>>();
        private readonly Dictionary<string, List<int>> struckRows = new Dictionary<string, List<int>>();

        private byte[] xlsxBytes;
        private List<string> sharedStrings;
        private List<bool> cellStrikeStyles;
        private Dictionary<string, string> relIdToPath;
        private readonly List<(string name, string relId)> sheetDefs = new List<(string, string)>();

        public FeederGoogleSheetController(string sheetSpreadsheetId)
        {
            xlsxBytes = DownloadXlsx(sheetSpreadsheetId);
            ReadMetadata();
            foreach ((string name, string _) in sheetDefs)
            {
                EnsureSheet(name);
            }
        }

        public static FeederGoogleSheetController CreateForSingleSheet(string sheetSpreadsheetId)
        {
            FeederGoogleSheetController controller = new FeederGoogleSheetController();
            controller.xlsxBytes = DownloadXlsx(sheetSpreadsheetId);
            controller.ReadMetadata();
            return controller;
        }

        private FeederGoogleSheetController()
        {
        }

        public List<string> GetAllSheetName()
        {
            return new List<string>(sheetNames);
        }

        public Dictionary<string, IList<IList<Object>>> GetAllSheetValueRange(List<string> names)
        {
            Dictionary<string, IList<IList<Object>>> result = new Dictionary<string, IList<IList<Object>>>();
            foreach (string name in names)
            {
                IList<IList<Object>> outRows = new List<IList<Object>>();
                if (sheetValues.TryGetValue(name, out List<List<string>> rows))
                {
                    foreach (List<string> row in rows)
                    {
                        List<Object> outRow = new List<Object>(row.Count);
                        foreach (string cell in row)
                        {
                            outRow.Add(cell);
                        }

                        outRows.Add(outRow);
                    }
                }

                result[name] = outRows;
            }

            return result;
        }

        public Dictionary<string, List<int>> GetStrikethroughRowsPerSheet(List<string> names)
        {
            Dictionary<string, List<int>> result = new Dictionary<string, List<int>>();
            foreach (string name in names)
            {
                result[name] = struckRows.TryGetValue(name, out List<int> rows) ? new List<int>(rows) : new List<int>();
            }

            return result;
        }

        // ---------- Download ----------

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Add("User-Agent", "Feeder-GoogleSheetImporter");
            return client;
        }

        private static byte[] DownloadXlsx(string spreadsheetId)
        {
            string url = $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=xlsx";
            byte[] data;
            try
            {
                data = Task.Run(async () =>
                {
                    using (HttpResponseMessage resp = await Http.GetAsync(url))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new Exception($"HTTP {(int)resp.StatusCode}");
                        }

                        return await resp.Content.ReadAsByteArrayAsync();
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                throw new Exception($"Không tải được sheet. Kiểm tra Spreadsheet ID và kết nối mạng.\n{e.Message}");
            }

            if (data == null || data.Length < 4 || data[0] != 0x50 || data[1] != 0x4B)
            {
                throw new Exception(
                    "Sheet chưa được chia sẻ công khai. Vào Chia sẻ → 'Bất kỳ ai có link' → quyền Người xem (Viewer) rồi thử lại.");
            }

            return data;
        }

        // ---------- Parse OOXML ----------

        private void ReadMetadata()
        {
            using (ZipArchive zip = new ZipArchive(new MemoryStream(xlsxBytes), ZipArchiveMode.Read))
            {
                sharedStrings = ReadSharedStrings(zip);
                cellStrikeStyles = ReadCellStrikes(zip);
                relIdToPath = ReadWorkbookRels(zip);
                foreach ((string name, string relId) in ReadWorkbookSheets(zip))
                {
                    sheetDefs.Add((name, relId));
                    sheetNames.Add(name);
                }
            }
        }

        public void EnsureSheet(string name)
        {
            if (string.IsNullOrEmpty(name) || sheetValues.ContainsKey(name))
            {
                return;
            }

            string relId = null;
            foreach ((string sheetName, string id) in sheetDefs)
            {
                if (sheetName == name)
                {
                    relId = id;
                    break;
                }
            }

            if (relId == null || !relIdToPath.TryGetValue(relId, out string path))
            {
                sheetValues[name] = new List<List<string>>();
                struckRows[name] = new List<int>();
                return;
            }

            using (ZipArchive zip = new ZipArchive(new MemoryStream(xlsxBytes), ZipArchiveMode.Read))
            {
                ParseSheet(zip.GetEntry(path), name, sharedStrings, cellStrikeStyles);
            }
        }

        private void ParseSheet(ZipArchiveEntry entry, string name, List<string> shared, List<bool> cellStrike)
        {
            List<List<string>> values = new List<List<string>>();
            List<int> struck = new List<int>();
            sheetValues[name] = values;
            struckRows[name] = struck;

            if (entry == null)
            {
                return;
            }

            XElement root = Load(entry).Root;
            XElement sheetData = root?.Element(Main + "sheetData");
            if (sheetData == null)
            {
                return;
            }

            SortedDictionary<int, List<string>> rowsByIndex = new SortedDictionary<int, List<string>>();
            SortedDictionary<int, bool> rowStruck = new SortedDictionary<int, bool>();
            int lastContentRow = -1;

            foreach (XElement row in sheetData.Elements(Main + "row"))
            {
                int rowIndex = (int?)row.Attribute("r") - 1 ?? -1;
                if (rowIndex < 0)
                {
                    continue;
                }

                List<string> cells = new List<string>();
                bool hasContent = false;
                bool allStruck = true;

                foreach (XElement c in row.Elements(Main + "c"))
                {
                    int col = ColumnIndex((string)c.Attribute("r"));
                    if (col < 0)
                    {
                        continue;
                    }

                    string val = ReadCellValue(c, shared);
                    while (cells.Count <= col)
                    {
                        cells.Add(string.Empty);
                    }

                    cells[col] = val;

                    if (!string.IsNullOrEmpty(val))
                    {
                        hasContent = true;
                        int s = (int?)c.Attribute("s") ?? 0;
                        bool struckCell = s >= 0 && s < cellStrike.Count && cellStrike[s];
                        if (!struckCell)
                        {
                            allStruck = false;
                        }
                    }
                }

                if (!hasContent)
                {
                    continue;
                }

                TrimTrailingEmpty(cells);
                rowsByIndex[rowIndex] = cells;
                rowStruck[rowIndex] = allStruck;
                if (rowIndex > lastContentRow)
                {
                    lastContentRow = rowIndex;
                }
            }

            for (int i = 0; i <= lastContentRow; i++)
            {
                values.Add(rowsByIndex.TryGetValue(i, out List<string> cells) ? cells : new List<string>());
                if (rowStruck.TryGetValue(i, out bool isStruck) && isStruck)
                {
                    struck.Add(i);
                }
            }
        }

        private static string ReadCellValue(XElement c, List<string> shared)
        {
            string t = (string)c.Attribute("t");
            switch (t)
            {
                case "s": // shared string
                {
                    XElement v = c.Element(Main + "v");
                    if (v != null && int.TryParse(v.Value, out int idx) && idx >= 0 && idx < shared.Count)
                    {
                        return shared[idx];
                    }

                    return string.Empty;
                }
                case "inlineStr":
                {
                    XElement inline = c.Element(Main + "is");
                    return inline != null ? ConcatText(inline) : string.Empty;
                }
                case "b":
                {
                    XElement v = c.Element(Main + "v");
                    return v != null && v.Value == "1" ? "TRUE" : "FALSE";
                }
                default:
                {
                    XElement v = c.Element(Main + "v");
                    return v != null ? v.Value : string.Empty;
                }
            }
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            List<string> result = new List<string>();
            ZipArchiveEntry entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return result;
            }

            XElement root = Load(entry).Root;
            if (root == null)
            {
                return result;
            }

            foreach (XElement si in root.Elements(Main + "si"))
            {
                result.Add(ConcatText(si));
            }

            return result;
        }

        private static List<bool> ReadCellStrikes(ZipArchive zip)
        {
            List<bool> result = new List<bool>();
            ZipArchiveEntry entry = zip.GetEntry("xl/styles.xml");
            if (entry == null)
            {
                return result;
            }

            XElement root = Load(entry).Root;
            if (root == null)
            {
                return result;
            }

            List<bool> fontStrike = new List<bool>();
            XElement fonts = root.Element(Main + "fonts");
            if (fonts != null)
            {
                foreach (XElement font in fonts.Elements(Main + "font"))
                {
                    XElement strike = font.Element(Main + "strike");
                    bool struck = strike != null;
                    if (struck)
                    {
                        string val = (string)strike.Attribute("val");
                        if (val == "0" || val == "false")
                        {
                            struck = false;
                        }
                    }

                    fontStrike.Add(struck);
                }
            }

            XElement cellXfs = root.Element(Main + "cellXfs");
            if (cellXfs != null)
            {
                foreach (XElement xf in cellXfs.Elements(Main + "xf"))
                {
                    int fontId = (int?)xf.Attribute("fontId") ?? 0;
                    result.Add(fontId >= 0 && fontId < fontStrike.Count && fontStrike[fontId]);
                }
            }

            return result;
        }

        private static Dictionary<string, string> ReadWorkbookRels(ZipArchive zip)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            ZipArchiveEntry entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (entry == null)
            {
                return result;
            }

            XElement root = Load(entry).Root;
            if (root == null)
            {
                return result;
            }

            foreach (XElement rel in root.Elements(PkgRel + "Relationship"))
            {
                string id = (string)rel.Attribute("Id");
                string target = (string)rel.Attribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target))
                {
                    continue;
                }

                target = target.Replace('\\', '/');
                target = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                result[id] = target;
            }

            return result;
        }

        private static IEnumerable<(string name, string relId)> ReadWorkbookSheets(ZipArchive zip)
        {
            List<(string, string)> result = new List<(string, string)>();
            ZipArchiveEntry entry = zip.GetEntry("xl/workbook.xml");
            if (entry == null)
            {
                return result;
            }

            XElement root = Load(entry).Root;
            XElement sheets = root?.Element(Main + "sheets");
            if (sheets == null)
            {
                return result;
            }

            foreach (XElement sheet in sheets.Elements(Main + "sheet"))
            {
                string name = (string)sheet.Attribute("name");
                string relId = (string)sheet.Attribute(Rel + "id");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(relId))
                {
                    result.Add((name, relId));
                }
            }

            return result;
        }

        // ---------- Helpers ----------

        private static XDocument Load(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            {
                return XDocument.Load(stream);
            }
        }

        private static string ConcatText(XElement element)
        {
            StringBuilder sb = new StringBuilder();
            foreach (XElement t in element.Descendants(Main + "t"))
            {
                sb.Append(t.Value);
            }

            return sb.ToString();
        }

        private static int ColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef))
            {
                return -1;
            }

            int idx = 0;
            bool any = false;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    idx = idx * 26 + (ch - 'A' + 1);
                    any = true;
                }
                else if (ch >= 'a' && ch <= 'z')
                {
                    idx = idx * 26 + (ch - 'a' + 1);
                    any = true;
                }
                else
                {
                    break;
                }
            }

            return any ? idx - 1 : -1;
        }

        private static void TrimTrailingEmpty(List<string> cells)
        {
            int last = cells.Count - 1;
            while (last >= 0 && string.IsNullOrEmpty(cells[last]))
            {
                cells.RemoveAt(last);
                last--;
            }
        }
    }
}
