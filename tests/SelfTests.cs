using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LlmUsageTray
{
    static class SelfTests
    {
        static int count;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        static void Throws(Action action, string message) { try { action(); } catch (ApiFailure) { count++; return; } throw new Exception(message); }
        public static int Run()
        {
            string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "last-result.txt"));
            string temp = Path.Combine(Path.GetTempPath(), "LlmUsageTray-test-" + Guid.NewGuid().ToString("N"));
            try {
                QueryRange range = Calendar.Range(new DateTime(2026, 11, 16, 12, 5, 31, DateTimeKind.Utc));
                Check(Calendar.Iso(range.DayStartUtc) == "2026-11-15T16:00:00Z", "Beijing day boundary");
                Check(Calendar.Iso(range.MonthStartUtc) == "2026-10-31T16:00:00Z", "Beijing month boundary");
                Check(Calendar.Range(new DateTime(2026, 12, 31, 16, 0, 0, DateTimeKind.Utc)).LocalDate == "2027-01-01", "Year rollover");
                Check(Calendar.Range(new DateTime(2026, 11, 16, 15, 59, 59, DateTimeKind.Utc)).LocalDate == "2026-11-16", "Before midnight");
                Check(Calendar.Range(new DateTime(2026, 11, 16, 16, 0, 0, DateTimeKind.Utc)).LocalDate == "2026-11-17", "At midnight");
                string json = "{\"application\":{\"name\":\"test_app\",\"apiKey\":\"do-not-cache-secret\"},\"totalRequests\":2,\"totalTokens\":1000000001,\"approxChargeInUSD\":1.25,\"stats\":{\"Group\":[{\"model\":\"Model\",\"totalRequests\":2,\"promptTokens\":10,\"completionTokens\":20,\"totalTokens\":1000000001,\"approxChargeInUSD\":1.25}]}}";
                Usage usage = UsageClient.Parse(200, json);
                Check(usage.Tokens == 1000000001 && usage.Models[0].Tokens == 1000000001, "Keep reported total instead of input+output");
                Check(usage.Models[0].Category == "Group" && usage.Cost == 1.25m, "Read grouped model costs");
                Check(Format.AverageCost(usage) == "$0.6250", "Average cost");
                Check(Format.AverageCost(new Usage()) == "—", "Zero requests average");
                Check(Format.Share(2, 0) == 0, "Zero total share");
                Check(Format.Tokens(1000000001) == "1.00B", "Large token formatting");
                Check(Budget.UsedPercent(700, 1000) == 70, "700/1000 means 70 percent used");
                Check(Budget.RemainingPercent(700, 1000) == 30, "Remaining is 30 percent");
                Check(Budget.UsedPercent(1200, 1000) == 120, "Do not clamp actual used percent");
                Check(Budget.RemainingPercent(1200, 1000) == -20 && Budget.RemainingMoney(1200, 1000) == -200, "Preserve negative remaining budget");
                Check(Budget.RemainingPercent(1000, 1000) == 0, "Exact budget exhaustion");
                Check(new Settings().MonthlyBudget == 1000, "Default budget");
                Check(new Settings().User == "ntid", "Generic request-header default");
                Settings migrated = new JavaScriptSerializer().Deserialize<Settings>("{\"User\":\"test\",\"RefreshMinutes\":5,\"ShowStrip\":true}");
                Check(migrated.MonthlyBudget == 1000, "Migrate old settings with default budget");
                string tooltip = Budget.Tooltip(new Usage { Cost = 17.3m }, new Usage { Cost = 1200 }, 1000, false);
                Check(tooltip.Contains("-20%") && tooltip.Contains("预算 $1,000.00") && tooltip.Contains("今日 $17.30"), "Tooltip preserves budget and overrun");
                Check(Budget.Tooltip(new Usage { Cost = 1234567890m }, new Usage { Cost = 2234567890m }, 1000000000, true).Length <= 63, "Long totals fit native tooltip");
                using (var icon = BudgetIcon.Create(1200, 1000, false)) Check(icon.Width == 32 && icon.Height == 32, "Generate overrun icon");
                using (var full = Render(0)) using (var partly = Render(700)) using (var low = Render(900)) using (var emptyBattery = Render(1000)) using (var debt = Render(1200)) {
                    Check(full.GetPixel(23, 14).G > full.GetPixel(23, 14).R, "Unspent budget shows full green battery");
                    Check(partly.GetPixel(5, 14).G > partly.GetPixel(5, 14).R && partly.GetPixel(20, 14).G < 60, "700 spent leaves a short fill anchored on the left");
                    Check(low.GetPixel(5, 14).R > low.GetPixel(5, 14).B && low.GetPixel(8, 14).G < 60, "Low remaining budget shows a small amber fill");
                    Check(emptyBattery.GetPixel(14, 14).G < 60 && emptyBattery.GetPixel(29, 15).R > 200, "Exhausted budget is empty with a red terminal");
                    Check(debt.GetPixel(6, 14).G < 60 && debt.GetPixel(15, 16).R > 200, "Overrun stays empty and draws a red minus sign");
                }
                Usage empty = UsageClient.Parse(404, "{\"application\":{\"name\":\"test_app\"},\"message\":\"Not found any usage of application on given date range.\"}");
                Check(empty.Requests == 0 && empty.Models.Count == 0, "Documented empty response");
                Throws(delegate { UsageClient.Parse(404, "{\"message\":\"Route not found\"}"); }, "Do not treat all 404 as zero");
                Throws(delegate { UsageClient.Parse(401, "{}"); }, "Unauthorized");
                Throws(delegate { UsageClient.Parse(200, "{\"stats\":{}}"); }, "Missing totals must not become zero");
                Throws(delegate { UsageClient.Parse(200, "<html>Login</html>"); }, "HTML response");
                Throws(delegate { UsageClient.Parse(302, "{}"); }, "No credential forwarding on redirect");
                Snapshot snapshot = new Snapshot { LocalDate = "2026-11-16", UpdatedUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), Today = usage, Month = usage };
                Check(Calendar.IsToday(snapshot, new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)), "Current date cache");
                Check(!Calendar.IsToday(snapshot, new DateTime(2026, 11, 16, 16, 0, 0, DateTimeKind.Utc)), "Do not show yesterday as today");
                Check(Calendar.IsMonth(snapshot, new DateTime(2026, 11, 16, 16, 0, 0, DateTimeKind.Utc)), "Retain month after day change");
                Check(!Calendar.IsMonth(snapshot, new DateTime(2026, 11, 30, 16, 0, 0, DateTimeKind.Utc)), "Do not show prior month");
                Store store = new Store(temp);
                store.SaveSettings(new Settings { User = "test", RefreshMinutes = 5 }, "test-key-never-real");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp, "secret.dpapi"))).Contains("test-key-never-real"), "Encrypted credential");
                Check(new Store(temp).Key == "test-key-never-real", "DPAPI round trip");
                Check(new Store(temp).Settings.MonthlyBudget == 1000, "Persist default budget");
                snapshot.ScopeId = store.ScopeId;
                store.SaveCache(snapshot);
                Check(!File.ReadAllText(Path.Combine(temp, "cache.json")).Contains("do-not-cache-secret"), "Do not persist API response key");
                Check(store.LoadCache().Month.Tokens == usage.Tokens, "Cache round trip");
                store.SaveCache(snapshot);
                File.WriteAllText(Path.Combine(temp, "cache.json"), "broken");
                Check(store.LoadCache().Today.Requests == 2, "Recover cache from backup");
                store.SaveSettings(new Settings { User = "other", RefreshMinutes = 5 }, "different-test-key");
                Check(store.LoadCache() == null, "Do not reuse another key or user cache");
                File.WriteAllText(Path.Combine(temp, "config.json"), "{\"MonthlyBudget\":0,\"RefreshMinutes\":5}");
                Check(new Store(temp).Settings.MonthlyBudget == 1000, "Invalid persisted budget cannot divide by zero");
                Check(new Store(temp).Settings.User == "ntid", "Missing username loads generic placeholder");
                File.WriteAllText(output, "PASS: " + count + " checks (date ranges, parsing, empty results, credentials, cache).", Encoding.UTF8);
                return 0;
            } catch (Exception ex) { File.WriteAllText(output, "FAIL: " + ex.Message, Encoding.UTF8); return 1; }
            finally {
                string full = Path.GetFullPath(temp);
                string root = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("LlmUsageTray-test-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
        static System.Drawing.Bitmap Render(decimal spent)
        {
            var image = new System.Drawing.Bitmap(32, 32);
            using (var graphics = System.Drawing.Graphics.FromImage(image)) BudgetIcon.Draw(graphics, spent, 1000, false);
            return image;
        }
    }
}
