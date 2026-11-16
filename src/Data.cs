using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LlmUsageTray
{
    public sealed class Settings
    {
        public string User { get; set; }
        public int RefreshMinutes { get; set; }
        public decimal MonthlyBudget { get; set; }
        public Settings() { User = "ntid"; RefreshMinutes = 5; MonthlyBudget = Budget.Default; }
    }

    public sealed class ModelUsage
    {
        public string Category { get; set; }
        public string Model { get; set; }
        public long Requests { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long Tokens { get; set; }
        public decimal Cost { get; set; }
    }

    public sealed class Usage
    {
        public string Application { get; set; }
        public long Requests { get; set; }
        public long Tokens { get; set; }
        public decimal Cost { get; set; }
        public List<ModelUsage> Models { get; set; }
        public Usage() { Models = new List<ModelUsage>(); }
    }

    public sealed class Snapshot
    {
        public string ScopeId { get; set; }
        public string LocalDate { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string DayStartUtc { get; set; }
        public string MonthStartUtc { get; set; }
        public string EndUtc { get; set; }
        public Usage Today { get; set; }
        public Usage Month { get; set; }
    }

    public sealed class QueryRange
    {
        public string LocalDate;
        public DateTime DayStartUtc;
        public DateTime MonthStartUtc;
        public DateTime EndUtc;
    }

    public static class Calendar
    {
        public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        public static DateTime LocalNow { get { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone); } }
        public static string Iso(DateTime utc) { return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }
        public static QueryRange Range(DateTime utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
            return new QueryRange {
                LocalDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayStartUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), Zone),
                MonthStartUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(local.Year, local.Month, 1), Zone),
                EndUtc = utc
            };
        }
        public static bool IsToday(Snapshot s, DateTime utc) { return s != null && s.LocalDate == Range(utc).LocalDate; }
        public static bool IsMonth(Snapshot s, DateTime utc) { return s != null && s.LocalDate != null && s.LocalDate.StartsWith(Range(utc).LocalDate.Substring(0, 7), StringComparison.Ordinal); }
    }

    public static class Format
    {
        public static string Money(decimal n) { return "$" + n.ToString("N2", CultureInfo.InvariantCulture); }
        public static string Tokens(long n)
        {
            if (n >= 1000000000) return (n / 1000000000m).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000) return (n / 1000000m).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000m).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }
        public static decimal Share(decimal n, decimal total) { return total > 0 ? 100m * n / total : 0; }
        public static string AverageCost(Usage u) { return u.Requests > 0 ? "$" + (u.Cost / u.Requests).ToString("0.0000", CultureInfo.InvariantCulture) : "—"; }
        public static string AverageTokens(Usage u) { return u.Requests > 0 ? (u.Tokens / (decimal)u.Requests).ToString("N0", CultureInfo.InvariantCulture) : "—"; }
    }

    public static class Budget
    {
        public const decimal Default = 1000m;
        public static decimal UsedPercent(decimal cost, decimal limit) { if (limit <= 0) throw new ArgumentOutOfRangeException("limit"); return cost / limit * 100m; }
        public static decimal RemainingPercent(decimal cost, decimal limit) { return 100m - UsedPercent(cost, limit); }
        public static decimal RemainingMoney(decimal cost, decimal limit) { return limit - cost; }
        static string CompactMoney(decimal value)
        {
            if (Math.Abs(value) >= 1000000000m) return "$" + (value / 1000000000m).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (Math.Abs(value) >= 1000000m) return "$" + (value / 1000000m).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (Math.Abs(value) >= 10000m) return "$" + (value / 1000m).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return Format.Money(value);
        }
        public static string Tooltip(Usage today, Usage month, decimal limit, bool stale)
        {
            string state = stale ? " · 旧数据" : "";
            string tail = month != null ? "\n剩余 " + RemainingPercent(month.Cost, limit).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "\n等待查询";
            string text = "今日 " + (today != null ? Format.Money(today.Cost) : "—") + "\n本月 " + (month != null ? Format.Money(month.Cost) : "—") + "\n预算 " + Format.Money(limit) + tail + state;
            if (text.Length <= 63) return text;
            text = "今日 " + (today != null ? CompactMoney(today.Cost) : "—") + "\n本月 " + (month != null ? CompactMoney(month.Cost) : "—") + "\n预算 " + CompactMoney(limit);
            if ((text + tail + state).Length <= 63) return text + tail + state;
            return text + state;
        }
    }

    public sealed class Store
    {
        public readonly string DirectoryPath;
        public Settings Settings;
        public string Key;
        public string LoadNotice;
        public string ScopeId
        {
            get {
                using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes((Key ?? "") + "\n" + (Settings.User ?? ""))));
            }
        }
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        public Store(string path)
        {
            DirectoryPath = path;
            Directory.CreateDirectory(path);
            Settings = new Settings();
            try {
                string file = Path.Combine(path, "config.json");
                if (File.Exists(file)) Settings = Json.Deserialize<Settings>(File.ReadAllText(file, Encoding.UTF8)) ?? new Settings();
                Settings.RefreshMinutes = Math.Max(1, Math.Min(60, Settings.RefreshMinutes));
                if (String.IsNullOrWhiteSpace(Settings.User)) Settings.User = "ntid";
                if (Settings.MonthlyBudget <= 0 || Settings.MonthlyBudget > 1000000000m) Settings.MonthlyBudget = Budget.Default;
                string secretFile = Path.Combine(path, "secret.dpapi");
                if (File.Exists(secretFile)) {
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(secretFile), null, DataProtectionScope.CurrentUser);
                    try { Key = Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain, 0, plain.Length); }
                }
            } catch { LoadNotice = "配置或加密密钥无法读取，请重新设置。"; }
        }
        public void SaveSettings(Settings s, string newKey)
        {
            if (newKey != null) {
                byte[] plain = Encoding.UTF8.GetBytes(newKey);
                try { AtomicWrite(Path.Combine(DirectoryPath, "secret.dpapi"), ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
                finally { Array.Clear(plain, 0, plain.Length); }
                Key = newKey;
            }
            AtomicWrite(Path.Combine(DirectoryPath, "config.json"), Encoding.UTF8.GetBytes(Json.Serialize(s)));
            Settings = s;
        }
        public Snapshot LoadCache()
        {
            foreach (string name in new[] { "cache.json", "cache.json.bak" }) {
                try {
                    string file = Path.Combine(DirectoryPath, name);
                    if (!File.Exists(file)) continue;
                    Snapshot s = Json.Deserialize<Snapshot>(File.ReadAllText(file, Encoding.UTF8));
                    if (s != null && s.ScopeId == ScopeId && s.Today != null && s.Month != null && s.Today.Models != null && s.Month.Models != null && s.LocalDate != null) return s;
                } catch { }
            }
            return null;
        }
        public void SaveCache(Snapshot s) { AtomicWrite(Path.Combine(DirectoryPath, "cache.json"), Encoding.UTF8.GetBytes(Json.Serialize(s))); }
        public static void AtomicWrite(string path, byte[] data)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllBytes(temp, data);
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class ApiFailure : Exception
    {
        public int RetrySeconds;
        public ApiFailure(string text, int seconds = 0) : base(text) { RetrySeconds = seconds; }
    }

    public sealed class UsageClient : IDisposable
    {
        public const string Endpoint = "https://llm-api.amd.com/api/UsageStats";
        readonly HttpClient client;
        public UsageClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.Timeout = TimeSpan.FromSeconds(40);
        }
        public async Task<Snapshot> Read(string key, string user, QueryRange range)
        {
            Task<Usage> day = Query(key, user, range.DayStartUtc, range.EndUtc);
            Task<Usage> month = Query(key, user, range.MonthStartUtc, range.EndUtc);
            await Task.WhenAll(day, month);
            return new Snapshot {
                LocalDate = range.LocalDate, UpdatedUtc = DateTime.UtcNow,
                DayStartUtc = Calendar.Iso(range.DayStartUtc), MonthStartUtc = Calendar.Iso(range.MonthStartUtc), EndUtc = Calendar.Iso(range.EndUtc),
                Today = day.Result, Month = month.Result
            };
        }
        async Task<Usage> Query(string key, string user, DateTime start, DateTime end)
        {
            string uri = Endpoint + "?start=" + Uri.EscapeDataString(Calendar.Iso(start)) + "&end=" + Uri.EscapeDataString(Calendar.Iso(end));
            try {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri)) {
                    request.Headers.Add("Ocp-Apim-Subscription-Key", key);
                    if (!String.IsNullOrWhiteSpace(user)) request.Headers.Add("user", user);
                    request.Headers.Add("api-version", "preview");
                    request.Headers.Add("Accept", "application/json");
                    using (HttpResponseMessage response = await client.SendAsync(request)) {
                        string body = await response.Content.ReadAsStringAsync();
                        int retry = 0;
                        if (response.Headers.RetryAfter != null) {
                            if (response.Headers.RetryAfter.Delta.HasValue) retry = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                            else if (response.Headers.RetryAfter.Date.HasValue) retry = (int)(response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                        }
                        return Parse((int)response.StatusCode, body, retry);
                    }
                }
            } catch (ApiFailure) { throw; }
            catch (TaskCanceledException) { throw new ApiFailure("查询超时，将自动重试。请检查公司网络 / VPN。 "); }
            catch (HttpRequestException) { throw new ApiFailure("连接失败，将自动重试。请检查公司网络 / VPN。 "); }
        }
        public static Usage Parse(int status, string body, int retry = 0)
        {
            if (status == 401 || status == 403) throw new ApiFailure("密钥无效或无查询权限，请检查设置。", 300);
            if (status == 429) throw new ApiFailure("接口限流，稍后自动重试。", Math.Max(60, Math.Min(3600, retry)));
            if (status >= 300 && status < 400) throw new ApiFailure("接口要求重定向，已停止转发密钥。请检查接口状态。", 300);
            IDictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(body) as IDictionary<string, object>; }
            catch { throw new ApiFailure("接口返回的数据无法解析。", 300); }
            if (root == null) throw new ApiFailure("接口返回的数据格式不正确。", 300);
            if (status == 404 && Text(root, "message") == "Not found any usage of application on given date range.") {
                var empty = new Usage();
                var app = GetObject(root, "application");
                empty.Application = app != null ? Text(app, "name") : "";
                return empty;
            }
            if (status != 200) throw new ApiFailure("查询失败：HTTP " + status.ToString(CultureInfo.InvariantCulture) + "，将自动重试。");
            try {
                Usage usage = new Usage { Requests = Count(root, "totalRequests"), Tokens = Count(root, "totalTokens"), Cost = Number(root, "approxChargeInUSD") };
                var app = GetObject(root, "application");
                usage.Application = app != null ? Text(app, "name") : "";
                var groups = GetObject(root, "stats");
                if (groups == null) throw new FormatException();
                foreach (var group in groups) {
                    var items = group.Value as object[];
                    if (items == null) throw new FormatException();
                    foreach (var item in items) {
                        var row = item as IDictionary<string, object>;
                        if (row == null) throw new FormatException();
                        usage.Models.Add(new ModelUsage { Category = group.Key, Model = Text(row, "model"), Requests = Count(row, "totalRequests"),
                            PromptTokens = Count(row, "promptTokens"), CompletionTokens = Count(row, "completionTokens"), Tokens = Count(row, "totalTokens"), Cost = Number(row, "approxChargeInUSD") });
                    }
                }
                return usage;
            } catch { throw new ApiFailure("接口字段缺失或格式发生变化，已保留上次数据。", 300); }
        }
        static IDictionary<string, object> GetObject(IDictionary<string, object> obj, string key) { object v; return obj.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        static string Text(IDictionary<string, object> obj, string key) { object v; return obj.TryGetValue(key, out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static decimal Number(IDictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) throw new FormatException();
            decimal n = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (n < 0) throw new FormatException();
            return n;
        }
        static long Count(IDictionary<string, object> obj, string key)
        {
            decimal n = Number(obj, key);
            if (Decimal.Truncate(n) != n) throw new FormatException();
            return checked((long)n);
        }
        public void Dispose() { client.Dispose(); }
    }
}
