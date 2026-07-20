using System;
using System.Collections.Generic;
using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using UnityEngine;
using Object = System.Object;

namespace Feeder
{
    public class FeederGoogleSheetController
    {
        private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
        private readonly string applicationName = "GoogleSheet Reader";
        private readonly string spreadsheetId;
        private readonly SheetsService sheetService;

        public FeederGoogleSheetController(string sheetSpreadsheetId, string credentialFilePath)
        {
            spreadsheetId = sheetSpreadsheetId;
            GoogleCredential credential;
            using (FileStream stream = new FileStream(credentialFilePath, FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);
            }

            sheetService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName
            });
        }

        public List<string> GetAllSheetName()
        {
            List<string> ranges = new List<string>();
            bool includeGridData = false;

            SpreadsheetsResource.GetRequest request = sheetService.Spreadsheets.Get(spreadsheetId);
            request.Ranges = ranges;
            request.IncludeGridData = includeGridData;

            Spreadsheet response = request.Execute();
            if (response.Sheets != null)
            {
                List<string> sheetNameList = new List<string>();
                foreach (Sheet sheet in response.Sheets)
                {
                    sheetNameList.Add(sheet.Properties.Title);
                }

                return sheetNameList;
            }

            return null;
        }

        public IList<IList<Object>> GetValueRange(string sheetName, string range)
        {
            SpreadsheetsResource.ValuesResource.GetRequest request =
                sheetService.Spreadsheets.Values.Get(spreadsheetId, $"{sheetName}!{range}");

            ValueRange response = request.Execute();
            IList<IList<Object>> values = response.Values;
            if (values != null && values.Count > 0)
            {
                return values;
            }

            Console.WriteLine("No data found.");
            return null;
        }

        public bool SetValueRange(string sheetName, IList<IList<object>> data)
        {
            ValueRange valueRange = new ValueRange { Values = data };
            string range = $"{sheetName}!A2:Z";

            SpreadsheetsResource.ValuesResource.UpdateRequest request =
                sheetService.Spreadsheets.Values.Update(valueRange, spreadsheetId, range);
            request.ValueInputOption =
                SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            UpdateValuesResponse response = request.Execute();
            if (response.UpdatedCells == null)
            {
                return false;
            }

            Debug.Log($"Update Sheet OK : {response.UpdatedRange}");
            return true;
        }

        public Dictionary<string, IList<IList<Object>>> GetAllSheetValueRange(List<string> sheetNames)
        {
            SpreadsheetsResource.ValuesResource.BatchGetRequest request =
                sheetService.Spreadsheets.Values.BatchGet(spreadsheetId);
            request.Ranges = sheetNames;
            BatchGetValuesResponse response = request.Execute();
            Dictionary<string, IList<IList<Object>>> sheetValueDict = new Dictionary<string, IList<IList<Object>>>();
            for (int i = 0; i < response.ValueRanges.Count; i++)
            {
                sheetValueDict.Add(sheetNames[i], response.ValueRanges[i].Values);
            }

            return sheetValueDict;
        }
    }
}
