using DATools;
using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using ShareKey = System.ValueTuple<string, string, string>; // tradeDate, secId, boardId

namespace MOEXDownloader
{
    static class Extensions
    {
        public static IEnumerable<IEnumerable<T>> Partition<T>(this IEnumerable<T> items, int partitionSize)
        {
            return items.Select((item, inx) => new { item, inx })
                        .GroupBy(x => x.inx / partitionSize)
                        .Select(g => g.Select(x => x.item));
        }
    }

    sealed class SessionTurnover
    {
        public decimal Total { get; }
        public int SessionsCount { get; }

        public SessionTurnover(decimal total, int sessionsCount)
        {
            Total = total;
            SessionsCount = sessionsCount;
        }
    }

    static class ShareTurnover
    {
        public static ShareKey KeyOf(XElement row) =>
            ((string)row.Attribute("TRADEDATE"), (string)row.Attribute("SECID"), (string)row.Attribute("BOARDID"));

        public static IReadOnlyDictionary<ShareKey, SessionTurnover> Aggregate(IEnumerable<XElement> sessionRows) =>
            sessionRows
                .Where(HasValue)
                .GroupBy(KeyOf, row => decimal.Parse((string)row.Attribute("VALUE"), CultureInfo.InvariantCulture))
                .ToDictionary(g => g.Key, g => new SessionTurnover(g.Sum(), g.Count()));

        static bool HasValue(XElement row) => !string.IsNullOrEmpty((string)row.Attribute("VALUE"));
    }

    class ResultPoint
    {
        public DateTime Moment { get; set; }
        public float Value { get; set; }
    }


    
    class MoexFeed
    {
        public static readonly ILog bLog = LogManager.GetLogger(typeof(Program));
        List<ResultPoint> values = new List<ResultPoint>();
        Options opt;
        XDocument resDoc = null;
        static readonly int[] ShareSessionIds = { 0, 1, 2 };

        public MoexFeed(Options _opt)
        {
            opt = _opt;
        }
        public void ProcessXML()
        {
            //values = new List<ResultPoint>();
            
            if (!Config.IsOneDay && string.IsNullOrEmpty(opt.Ticker))
            {
                bLog.Warn($"Multitickers & Multidays - processing day by day");
                DateTime start = Config.DateStart;
                DateTime end = Config.DateEnd;
                for (var dd = start; dd <= end; dd = dd.AddDays(1))
                {
                    Config.DateStart = dd;
                    Config.DateEnd = dd;
                    InternalProcessXML();
                }
            }
            else
            {
                InternalProcessXML();
            }
        }
        XDocument CreateXMLTemplate()
        {
            return new XDocument(
                   new XElement("document",
                     new XElement("data", new XAttribute("id", "history")
                     , new XElement("rows", null)
                     )
                   )
                 );
        }
        void InternalProcessXML()
        {
            var session = Config.GetSession(opt);
            bLog.Info($"Using {GetURL(opt, 0, session)}");
            resDoc = CreateXMLTemplate();

            using (var client = new CookieAwareWebClient())
            {
                Auth(client);

                var turnovers = LoadSessionTurnovers(opt, client);

                var dataNode = resDoc.XPathSelectElement("/document/data/rows");
                dataNode.Add(ReadAllRows(opt, client, session));
                var total = dataNode.Elements().Count();
                var dataRoot = resDoc.XPathSelectElement("/document/data");
                dataRoot.Add(new XAttribute("total", total));

                StopUpdateProgress();
                bLog.Warn($"Downloaded {total} rows");
                if (dataNode.HasElements)
                {
                    FixResults(resDoc, opt.InstrumentType);
                    ApplySessionTurnovers(resDoc, turnovers);
                    if(opt.InstrumentType==InstrumentType.SwapXML)
                    {
                        MapSwaps(resDoc);
                    }
                    ProcessResults(resDoc);
                }

            }
        }

        IEnumerable<XElement> ReadAllRows(Options opt, CookieAwareWebClient client, int? session = null)
        {
            var start = 0;
            while (true)
            {
                var (items, pageSize, total) = GetDataPage(opt, client, start, session);
                foreach (var item in items)
                    yield return item;

                start += pageSize;
                if (pageSize <= 0 || start >= total)
                    yield break;
            }
        }

        IReadOnlyDictionary<ShareKey, SessionTurnover> LoadSessionTurnovers(Options opt, CookieAwareWebClient client)
        {
            if (opt.InstrumentType != InstrumentType.Shares)
                return new Dictionary<ShareKey, SessionTurnover>();

            return ShareTurnover.Aggregate(ShareSessionIds.SelectMany(s => ReadAllRows(opt, client, s)));
        }

        private void MapSwaps(XDocument resDoc)
        {

            var rowsNode = resDoc.XPathSelectElement("/document/data/rows");
            foreach (var n in rowsNode.Descendants())
            {
                var curve = n.Attribute("swap_curve");
                if (curve != null)
                {
                    var map = Config.configuration.Mapping.FindByCurve(curve.Value);
                    if (map != null)
                    {
                        n.SetAttributeValue("swap_curve", map.NewTicker);
                    }

                }

                if(!String.IsNullOrEmpty(Config.TickerSuffix))
                {
                    var new_curve = n.Attribute("swap_curve").Value + Config.TickerSuffix;
                    n.SetAttributeValue("swap_curve", new_curve);
                }

            }
        }

        void ProcessSwapXML()
        {
            var session = Config.GetSession(opt);
            bLog.Info($"Using {GetURL(opt, 0, session)}");
            resDoc = CreateXMLTemplate();

            var start = 0;
            int total;
            using (var client = new CookieAwareWebClient())
            {

                Auth(client);

                do
                {
                    int pageSize;
                    IEnumerable<XElement> items;
                    (items, pageSize, total) = GetDataPage(opt, client, start, session);
                    var dataNode = resDoc.XPathSelectElement("/document/data/rows");
                    dataNode.Add(items);
                    start += pageSize;
                } while (start <= total);
                var dataRoot = resDoc.XPathSelectElement("/document/data");
                dataRoot.Add(new XAttribute("total", total));

                StopUpdateProgress();
                bLog.Warn($"Downloaded {total} rows");
                var rowsNode = resDoc.XPathSelectElement("/document/data/rows");
                if (rowsNode.HasElements)
                {
                    FixResults(resDoc, opt.InstrumentType);
                    ProcessResults(resDoc);
                }

            }
        }


        //private void SaveResult

        private void FixResults(XDocument resDoc, InstrumentType instrumentType)
        {
            var rowsNode = resDoc.XPathSelectElement("/document/data/rows");
            foreach(var n in rowsNode.Descendants())
            {
                if(instrumentType == InstrumentType.IndexXMLRT)
                {
                    FixIndexXMLRT(n);
                    continue;
                }

                if (instrumentType == InstrumentType.SharesOTC)
                {
                    FixSharesOTC(n);
                    continue;
                }

                var curr = n.Attribute("CURRENCYID");
                if(curr == null)
                {
                    n.SetAttributeValue("CURRENCYID", "SUR");
                }
                var last = n.Attribute("LAST");
                var close = n.Attribute("CLOSE");
                if (close == null && last != null)
                {
                    n.SetAttributeValue("CLOSE", last.Value);
                }

                //var vol = n.Attribute("VOLUME");
                var voltoday = n.Attribute("VOLTODAY");
                if (voltoday != null)
                {
                    n.SetAttributeValue("VOLUME", voltoday.Value);
                }

                //var val = n.Attribute("VALUE");
                var valtoday = n.Attribute("VALTODAY");
                if (valtoday != null)
                {
                    n.SetAttributeValue("VALUE", valtoday.Value);
                }
                var volrur = n.Attribute("VOLRUR");
                if (volrur != null)
                {
                    n.SetAttributeValue("VALUE", volrur.Value);
                }

                var td = n.Attribute("TRADEDATE");
                var tdtm = n.Attribute("SYSTIME");
                if (td == null && tdtm != null)
                {
                    n.SetAttributeValue("TRADEDATE", tdtm.Value.Substring(0,10));
                }
                //VOLTODAY = VOLUME "3108221"
                //VALTODAY = VALUE "1595174389"
                // SYSTIME => TRADEDATE
            }
        }
        private void ApplySessionTurnovers(XDocument doc, IReadOnlyDictionary<ShareKey, SessionTurnover> turnovers)
        {
            foreach (var row in doc.XPathSelectElements("/document/data/rows/row"))
            {
                if (!turnovers.TryGetValue(ShareTurnover.KeyOf(row), out var turnover))
                    continue;

                WarnIfInconsistent(row, turnover);
                row.SetAttributeValue("VALUE", turnover.Total.ToString("0.##", CultureInfo.InvariantCulture));
            }
        }

        private void WarnIfInconsistent(XElement row, SessionTurnover turnover)
        {
            if (turnover.SessionsCount > ShareSessionIds.Length)
            {
                var key = ShareTurnover.KeyOf(row);
                bLog.Warn($"too many sessions value: {key.Item2} {key.Item3} {turnover.SessionsCount}");
            }

            var current = (string)row.Attribute("VALUE");
            if (!string.IsNullOrEmpty(current) &&
                decimal.Parse(current, CultureInfo.InvariantCulture) < turnover.Total)
            {
                bLog.Warn($"value:{current} valueBySessions:{turnover.Total}");
            }
        }

        private void FixSharesOTC(XElement n)
        {
            var curr = n.Attribute("CURRENCYID");
            if (curr == null)
            {
                n.SetAttributeValue("CURRENCYID", "SUR");
            }
            var last = n.Attribute("LAST");
            var close = n.Attribute("CLOSE");
            if (close == null && last != null)
            {
                n.SetAttributeValue("CLOSE", last.Value);
            }

            var legalClosePrice = n.Attribute("LEGALCLOSEPRICE");
            if (close != null && string.IsNullOrEmpty(legalClosePrice.Value))
            {
                n.SetAttributeValue("LEGALCLOSEPRICE", close.Value);
            }

            var voltoday = n.Attribute("VOLTODAY");
            if (voltoday != null)
            {
                n.SetAttributeValue("VOLUME", voltoday.Value);
            }

            var valtoday = n.Attribute("VALTODAY");
            if (valtoday != null)
            {
                n.SetAttributeValue("VALUE", valtoday.Value);
            }
            var volrur = n.Attribute("VOLRUR");
            if (volrur != null)
            {
                n.SetAttributeValue("VALUE", volrur.Value);
            }

            var td = n.Attribute("TRADEDATE");
            var tdtm = n.Attribute("SYSTIME");
            if (td == null && tdtm != null)
            {
                n.SetAttributeValue("TRADEDATE", tdtm.Value.Substring(0, 10));
            }
        }

        private void FixIndexXMLRT(XElement n)
        {
            var curr = n.Attribute("CURRENCYID");
            if (curr == null)
            {
                n.SetAttributeValue("CURRENCYID", "RUB");
            }

            var current = n.Attribute("CURRENTVALUE");

            var close = n.Attribute("CLOSE");
            if (close == null && current != null)
            {
                n.SetAttributeValue("CLOSE", current.Value);
            }

            var voltoday = n.Attribute("VOLTODAY");
            if (voltoday != null)
            {
                n.SetAttributeValue("VOLUME", voltoday.Value);
            }

            var valtoday = n.Attribute("VALTODAY");
            if (valtoday != null)
            {
                n.SetAttributeValue("VALUE", valtoday.Value);
            }
            var volrur = n.Attribute("VOLRUR");
            if (volrur != null)
            {
                n.SetAttributeValue("VALUE", volrur.Value);
            }

            var td = n.Attribute("TRADEDATE");
            var tdtm = n.Attribute("SYSTIME");
            if (td == null && tdtm != null)
            {
                n.SetAttributeValue("TRADEDATE", tdtm.Value.Substring(0, 10));
            }
        }

        private void ProcessResults(XDocument resDoc)
        {
            string responseLog = "";
            string file_name = Config.GetFileName(opt);

            string dirpath = Path.GetDirectoryName(file_name);
            if (!Directory.Exists(dirpath))
                Directory.CreateDirectory(dirpath);

            var conf = Config.GetConfByType(opt);

            if (!opt.NoSave)
            {
                bLog.Warn($"Saving to {file_name}");
                if(opt.InstrumentType == InstrumentType.SwapXML)
                {
                    SaveSwaps(resDoc, file_name);
                }
                else
                {
                    if (conf.OutputType == OutputFileType.Out)
                    {
                        SaveToOut(resDoc, file_name);
                    }
                    else
                    {
                        resDoc.Save(file_name);
                    }

                }

            }


            if (opt.FeedUpload)
            {
                try
                {
                    var chunks = ChunkRows(resDoc, Config.BatchSize);
                    foreach(var chunk in chunks)
                    {
                        responseLog = FeedUpload(chunk, responseLog, file_name, conf);
                    }

                }
                catch (System.ServiceModel.FaultException ex)
                {
                    //responseLog += $"Error: {ex.Message}";
                    bLog.Error($"FeedUp: {ex.Message}");
                    throw;
                }
            }

            if (opt.ZipResult)
            {
                var outZip = CompressFile(file_name);
                bLog.Warn($"Compress to {outZip}");
            }

            if (opt.EmailReport)
                Helpers.SendReport(opt, file_name, responseLog);


        }

        private void SaveSwaps(XDocument resDoc, string file_name)
        {
            using (Stream file = File.Create(file_name))
            {
                SaveSwapToOutStream(resDoc, file);
            }

        }
        private void SaveSwapToOutStream(XDocument resDoc, Stream ms)
        {
            StringBuilder stringBuilder = new StringBuilder();
            CultureInfo ic = CultureInfo.InvariantCulture;
            string format = @"yyyy-MM-dd";

            var rowsNode = resDoc.XPathSelectElement("/document/data/rows");
            foreach (var n in rowsNode.Descendants())
            {
                DateTime date = DateTime.ParseExact(n.Attribute("tradedate").Value, format, ic);
                string curve = n.Attribute("swap_curve").Value;
                string tenor = n.Attribute("tenor").Value;
                string ticker = $"{curve}_{tenor}";
                string value = n.Attribute("swap_rate").Value;
                //FXJPEMCS Index|0|1|23/03/2022|52.169|
                stringBuilder.AppendLine($"{ticker}|0|1|{date:dd/MM/yyyy}|{value}|");
            }
            ASCIIEncoding encoding = new ASCIIEncoding();
            ms.Write(encoding.GetBytes(stringBuilder.ToString()), 0, stringBuilder.Length);
            //File.WriteAllText(file_name, stringBuilder.ToString());
        }

        private string FeedUpload(XDocument resDoc, string responseLog, string file_name, TypeElement conf)
        {
            using (Stream stream = new MemoryStream())
            {
                if (opt.InstrumentType == InstrumentType.SwapXML)
                {
                    SaveSwapToOutStream(resDoc, stream);
                }
                else
                {
                    if (conf.OutputType == OutputFileType.Out)
                    {
                        SaveToOutStream(resDoc, stream);
                    }
                    else
                    {
                        resDoc.Save(stream);
                    }
                }

                stream.Position = 0;
                responseLog += Helpers.FeedUpload(opt, file_name, stream);
            }
            bLog.Warn($"FeedUp: {responseLog}");
            return responseLog;
        }


       
        private List<XDocument> ChunkRows(XDocument resDoc, int size)
        {
            var retDocs = new List<XDocument>();
            var items = resDoc.Element("document").Element("data").Elements("rows");
            int offset = 0;
            do
            {
                var xDoc = CreateXMLTemplate();
                var rowsNode = xDoc.XPathSelectElement("/document/data/rows");
                var chunk = items.Elements().Skip(offset).Take(size);
                rowsNode.Add(chunk);
                bLog.Warn($"Batch: {offset} {rowsNode.Elements().Count()}");
                retDocs.Add(xDoc);
                offset += size;
            } while (offset<=items.Elements().Count());

            return retDocs;
        }

        private void SaveToOut(XDocument resDoc, string file_name)
        {
            using (Stream file = File.Create(file_name))
            {
                SaveToOutStream(resDoc, file);
            }

        }


        private void SaveToOutStream(XDocument resDoc, Stream ms)
        {
            StringBuilder stringBuilder = new StringBuilder();
            CultureInfo ic = CultureInfo.InvariantCulture;
            string format = @"yyyy-MM-dd";

            var rowsNode = resDoc.XPathSelectElement("/document/data/rows");
            foreach (var n in rowsNode.Descendants())
            {
                DateTime date = DateTime.ParseExact(n.Attribute("TRADEDATE").Value, format, ic);
                string ticker = n.Attribute("SECID").Value;
                string value = n.Attribute("CLOSE").Value;
                //FXJPEMCS Index|0|1|23/03/2022|52.169|
                stringBuilder.AppendLine($"{ticker}|0|1|{date:dd/MM/yyyy}|{value}|");
            }
            ASCIIEncoding encoding = new ASCIIEncoding();
            ms.Write(encoding.GetBytes(stringBuilder.ToString()), 0, stringBuilder.Length);
            //File.WriteAllText(file_name, stringBuilder.ToString());
        }

        private string CompressFile(string sourceFileName)
        {
            string zipFileName = Path.ChangeExtension(sourceFileName, ".zip");
            if (File.Exists(zipFileName))
            {
                File.Delete(zipFileName);
            }
            using (var archive = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(sourceFileName, Path.GetFileName(sourceFileName));
            }
            File.Delete(sourceFileName);
            return zipFileName;
        }

        public (IEnumerable<XElement> items, int pageSize, int total) GetDataPage(Options opt, CookieAwareWebClient wc, int start, int? session=null)
        {
            int pageSize;
            int total;
            var conf = Config.configuration.Types.FindByType(opt.InstrumentType.ToString());
            string data_id = "history";
            if (!string.IsNullOrEmpty(conf.DataId))
            {
                data_id = conf.DataId;
            }

            string url = GetURL(opt, start, session);

            string response = DownloadData();
            var xDoc = XDocument.Parse(response);
            var items = (IEnumerable)xDoc.XPathEvaluate($"/document/data[@id=\"{data_id}\"]/rows/row");
            if (data_id == "history")
            {
                total = int.Parse(xDoc.XPathSelectElement("/document/data[@id=\"history.cursor\"]/rows/row").Attribute("TOTAL").Value);
                pageSize = int.Parse(xDoc.XPathSelectElement("/document/data[@id=\"history.cursor\"]/rows/row").Attribute("PAGESIZE").Value);
            }
            else
            {
                total = items.Cast<XElement>().Count();
                pageSize = total + 1;
            }

            UpdateProgress($"Rows from {start} from {total}");

            return (items: items.Cast<XElement>(), pageSize: pageSize, total: total);

            string DownloadData()
            {
                string result = "";
                int attemptsCount = Config.CountDownloadAttempts;
                while (attemptsCount > 0)
                {
                    try
                    {

                        result = wc.DownloadString(url);
                        foreach (var item in wc.ResponseHeaders) {
                            if (item.ToString().Contains("Micex") || item.ToString().Contains("X-Request-ID"))
                            {
                                bLog.Debug($"{item}:{wc.ResponseHeaders.Get(item.ToString())}");
                            }
                        }
                        if (wc.ResponseHeaders.Get("X-MicexPassport-Marker") == "denied" && !conf.IgnoreSubscription)
                        {
                            throw new UnauthorizedAccessException($"MOEX subscription denied {url}");
                        }
                        break;
                    }
                    catch (UnauthorizedAccessException )
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        bLog.Error(ex.Message);
                        attemptsCount--;
                        if (attemptsCount < 1)
                            throw;
                    }
                }
                return result;
            }
        }

        void UpdateProgress(string text)
        {
            if (!opt.NoCursor)
            {
                Console.Write("\r" + text);
            }
        }
        void StopUpdateProgress()
        {
            if (!opt.NoCursor)
            {
                Console.WriteLine();
            }
        }

        void Auth(CookieAwareWebClient wc)
        {
            try
            {
                //bLog.Debug($"{Config.micexUserName}, {Config.micexPassword}");
                    
                string uri = @"https://passport.moex.com/authenticate";
                wc.Credentials = new NetworkCredential(Config.micexUserName, Config.micexPassword);
                var resp = wc.DownloadString(uri);
                
                bLog.Debug(resp);

            }
            catch(Exception ex)
            {
                bLog.Warn($"Auth {Config.micexUserName}");
                bLog.Error(ex);
                throw;
            }
        }

        string GetURL(Options opt, int start = 0, int? sessionId = null)
        {
            var conf = Config.configuration.Types.FindByType(opt.InstrumentType.ToString());
            string base_url;

            if (!string.IsNullOrEmpty(conf.URL))
            {
                base_url = conf.URL;
            }
            else
            {
                string engines = (!string.IsNullOrEmpty(conf.Engines)) ? $"/engines/{conf.Engines}" : "";
                string markets = (!string.IsNullOrEmpty(conf.Markets)) ? $"/markets/{conf.Markets}" : "";
                string boards = (!string.IsNullOrEmpty(conf.Boards)) ? $"/boards/{conf.Boards}" : "";
                string session = (sessionId != null) ? $"/sessions/{sessionId}" : "";
                string instrument = (!string.IsNullOrEmpty(opt.Ticker)) ? $"/securities/{opt.Ticker}.xml" : "/securities.xml";
                base_url = $"{Config.configuration.MicexURL.BaseURL}{engines}{markets}{boards}{session}{instrument}";
            }
            string date_param = $"&from={Config.DateStart:yyyy-MM-dd}&till={Config.DateEnd:yyyy-MM-dd}";
            if (Config.IsOneDay)
            {
                if (string.IsNullOrEmpty(opt.Ticker))
                {
                    date_param = $"&date={Config.DateStart:yyyy-MM-dd}";
                }
                else
                {
                    date_param = $"&from={Config.DateStart:yyyy-MM-dd}&till={Config.DateStart:yyyy-MM-dd}";
                }
            }
            string start_param = $"&start={start}";

            string url = $"{base_url}?{Config.configuration.MicexURL.BaseParams}{date_param}{start_param}";

            return url;
        }



        /*static void DownloadXML()
        {
            var resDoc = XDocument.Load("secs.xml");
            var url = "http://iss.moex.com/iss/history/engines/stock/markets/shares/boards/tqbr/securities.xml?date={date}&start={start}";
            //var url = ConfigurationManager.AppSettings["bondUrl"];
            url = url.Replace("{date}", "2022-02-21");
            var start = 0;
            int total;
            do
            {
                int pageSize;

                var items = GetDataPage(client, url, start, out pageSize, out total);
                var dataNode = resDoc.XPathSelectElement("/document/data/rows");
                foreach (var item in items)
                    dataNode.Add(item);
                start += pageSize;
            } while (start <= total);
            resDoc.Save(Guid.NewGuid().ToString() + ".xml");
        }
        public static XmlNodeList GetDataPage(WebClient wc, string url, int start, out int pageSize, out int total)
        {
            url = url.Replace("{start}", start.ToString());
            var resp = wc.DownloadString(url);
            XmlDocument doc = JsonConvert.DeserializeXmlNode(resp, "data");
            var info = doc.SelectSingleNode("/document/data[2]/rows/row/@TOTAL"); 
            total = int.Parse(info.Attributes["TOTAL"].Value);
            pageSize = int.Parse(info.Attributes["PAGESIZE"].Value);

            var items = doc.SelectNodes("/document/data[1]/rows/row");

            return items;
        }
                internal static void ProcessXMLxxx(Options opt)
        {
            var conf = Config.configuration.Types.FindByType(opt.InstrumentType.ToString());
            string url = conf.URL;

            using (var client = new CookieAwareWebClient())
            {
                client.Encoding = Encoding.UTF8;

                Auth(client);

                var resDoc = XDocument.Load("secs.xml");

                var start = 0;
                int total;
                do
                {
                    int pageSize;
                    var resp = client.DownloadString(url);
                    var xDoc = XDocument.Load(resp);

                    var items = GetDataPage(client, url, start, out pageSize, out total);
                    var dataNode = resDoc.XPathSelectElement("/document/data/rows");
                    foreach (var item in items)
                        dataNode.Add(item);
                    start += pageSize;
                } while (start <= total);

                //string xml = client.DownloadString(url);
                //return xml;

            }

        }

        */

    }


}
