using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Drawing;
using System.Text.Json;

var progress = new Progress<ProgressReport>(report =>
{
    if (report.Error != null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"抓取页面 {report.CurrentPage} 时发生错误: {report.Error.Message}");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"已获取 {report.CurrentPage}/{report.TotalPages} 页 ({Math.Round((double)report.CurrentPage / report.TotalPages * 100, 2)}%)");
        Console.ResetColor();
    }
});

using var crawler = new BeatsaverCrawler(progress);
await crawler.CrawlAllMapsAsync("localcache.saver");
Console.WriteLine("完整本地缓存已保存到 localcache.saver");

public class ProgressReport
{
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public Exception Error { get; set; }
}

public class BeatsaverCrawler : IDisposable
{
    private const string ApiUrl = "https://api.beatsaver.com/search/text/";
    private const int PageSize = 100;
    private readonly HttpClient _client;
    private readonly IProgress<ProgressReport> _progress;
    private readonly ConcurrentBag<string> _tempFiles = new();

    public BeatsaverCrawler(IProgress<ProgressReport> progress)
    {
        _progress = progress;
        _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 5
        });
    }

    public async Task CrawlAllMapsAsync(string outputPath)
    {
        var totalPages = await GetTotalPagesAsync();
        var tempFiles = new ConcurrentDictionary<int, string>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 3 };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, totalPages),
            options,
            async (page, ct) =>
            {
                tempFiles.TryAdd(page, await ProcessPageAsync(page, totalPages));
            });
        await WriteCompressedJsonAsync(outputPath, tempFiles, totalPages);
    }

    public static async Task WriteCompressedJsonAsync(
    string outputPath,
    ConcurrentDictionary<int, string> tempFiles,
    int totalPages)
    {
        var docs = new List<object>();

        foreach (var page in Enumerable.Range(0, totalPages))
        {
            if (tempFiles.TryGetValue(page, out var file))
            {
                var json = await File.ReadAllTextAsync(file);
                var doc = JsonConvert.DeserializeObject<dynamic>(json);
                docs.AddRange(doc.docs.ToObject<List<object>>());
                File.Delete(file);
            }
        }

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore
        };

        await File.WriteAllTextAsync(outputPath,
        JsonConvert.SerializeObject(new { docs }, settings));
    }

    private async Task<int> GetTotalPagesAsync()
    {
        var response = await _client.GetStringAsync($"{ApiUrl}0?pageSize={PageSize}");
        var doc = JsonDocument.Parse(response);
        return (int)Math.Ceiling(doc.RootElement.GetProperty("info").GetProperty("total").GetInt32() / (double)PageSize);
    }

    private async Task<string> ProcessPageAsync(int page, int totalPages)
    {
        try
        {
            var response = await _client.GetStringAsync($"{ApiUrl}{page}?pageSize={PageSize}");
            var tempFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempFile, response);

            _progress?.Report(new ProgressReport
            {
                CurrentPage = page + 1,
                TotalPages = totalPages
            });

            return tempFile;
        }
        catch (Exception ex)
        {
            _progress?.Report(new ProgressReport { Error = ex });
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}