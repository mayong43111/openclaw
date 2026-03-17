using Azure.Data.Tables;
using OpenClaw.Console.Models;

namespace OpenClaw.Console.Services;

public class VmTableService
{
    private readonly TableClient _table;

    public VmTableService(TableServiceClient tableServiceClient, IConfiguration config)
    {
        var tableName = config["Storage:TableName"] ?? "vmrecords";
        _table = tableServiceClient.GetTableClient(tableName);
    }

    public async Task<List<VmRecord>> GetAllAsync()
    {
        var records = new List<VmRecord>();
        await foreach (var entity in _table.QueryAsync<VmRecord>(filter: $"PartitionKey eq 'vm'"))
        {
            records.Add(entity);
        }
        return records.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<VmRecord?> GetAsync(string vmName)
    {
        try
        {
            var response = await _table.GetEntityAsync<VmRecord>("vm", vmName);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpsertAsync(VmRecord record)
    {
        await _table.UpsertEntityAsync(record, TableUpdateMode.Merge);
    }

    public async Task DeleteAsync(string vmName)
    {
        await _table.DeleteEntityAsync("vm", vmName);
    }

    /// <summary>
    /// Atomically increment and return the next VM number.
    /// Stored as a counter entity (PartitionKey="meta", RowKey="counter").
    /// </summary>
    public async Task<int> GetNextVmNumberAsync()
    {
        const string pk = "meta";
        const string rk = "counter";

        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(pk, rk);
            var entity = response.Value;
            var current = entity.GetInt32("LastNumber") ?? 0;
            var next = current + 1;
            entity["LastNumber"] = next;
            await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return next;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // First time: seed from existing VMs to avoid collisions
            var all = await GetAllAsync();
            var maxExisting = all.Count > 0
                ? all.Max(v =>
                {
                    var parts = v.Name.Split('-');
                    return int.TryParse(parts[^1], out var n) ? n : 0;
                })
                : 0;
            var next = maxExisting + 1;
            var entity = new TableEntity(pk, rk) { { "LastNumber", next } };
            await _table.AddEntityAsync(entity);
            return next;
        }
    }
}
