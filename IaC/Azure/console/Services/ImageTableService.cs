using Azure.Data.Tables;
using OpenClaw.Console.Models;

namespace OpenClaw.Console.Services;

public class ImageTableService
{
    private readonly TableClient _table;

    public ImageTableService(TableServiceClient tableServiceClient, IConfiguration config)
    {
        var tableName = config["Storage:ImageTableName"] ?? "images";
        _table = tableServiceClient.GetTableClient(tableName);
    }

    public async Task<List<ImageRecord>> GetActiveAsync()
    {
        var records = new List<ImageRecord>();
        await foreach (var entity in _table.QueryAsync<ImageRecord>(
            filter: $"PartitionKey eq 'image' and IsActive eq true"))
        {
            records.Add(entity);
        }
        return records.OrderByDescending(r => r.Timestamp).ToList();
    }

    public async Task<ImageRecord?> GetAsync(string imageName)
    {
        try
        {
            var response = await _table.GetEntityAsync<ImageRecord>("image", imageName);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpsertAsync(ImageRecord record)
    {
        await _table.UpsertEntityAsync(record, TableUpdateMode.Merge);
    }
}
