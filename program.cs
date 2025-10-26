using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SAPbobsCOM;
using System.Globalization;
using System.Net;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AppConfig>();
builder.Services.AddSingleton<SqliteProvider>();
builder.Services.AddSingleton<SapConnector>();
builder.Services.AddSingleton<ExternalApiClient>();
builder.Services.AddHostedService<CronWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors();
var app = builder.Build();
app.UseCors(p => p.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod());

var cfg = app.Services.GetRequiredService<AppConfig>();
app.MapPost("/suppliers/import", async (HttpRequest req, SqliteProvider db) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("Send as multipart/form-data with file field 'file' (CSV).");
    var form = await req.ReadFormAsync();
    var file = form.Files["file"];
    if (file == null) return Results.BadRequest("Missing file field 'file'.");
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    ms.Position = 0;
    using var sr = new StreamReader(ms);
    var added = 0;
    while (!sr.EndOfStream)
    {
        var line = (await sr.ReadLineAsync())?.Trim();
        if (string.IsNullOrWhiteSpace(line)) continue;
        var parts = line.Split(',');
        var cardCode = parts[0].Trim();
        var cardName = parts.Length > 1 ? parts[1].Trim() : null;
        if (await db.AddSupplierAsync(cardCode, cardName)) added++;
    }
    return Results.Ok(new { added });
});

app.MapPost("/suppliers/add", async (SupplierDto dto, SqliteProvider db) =>
{
    var ok = await db.AddSupplierAsync(dto.CardCode, dto.CardName);
    if (!ok) return Results.Conflict("Already exists");
    return Results.Ok();
});

app.MapPost("/suppliers/remove", async (SupplierDto dto, SqliteProvider db) =>
{
    var ok = await db.RemoveSupplierAsync(dto.CardCode);
    if (!ok) return Results.NotFound();
    return Results.Ok();
});

app.MapGet("/suppliers", async (SqliteProvider db) =>
{
    var list = await db.GetSuppliersAsync();
    return Results.Ok(list);
});

app.MapGet("/token", async (HttpRequest req, SqliteProvider db) =>
{
    string user = req.Query["user"];
    string pass = req.Query["pass"];
    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return Results.BadRequest("user & pass query params required");
    var valid = await db.ValidateUserAsync(user, pass);
    if (!valid) return Results.Unauthorized();
    var token = Guid.NewGuid().ToString("N");
    await db.StoreIssuedTokenAsync(token, user, DateTime.UtcNow.AddHours(8));
    return Results.Ok(new { token });
});

app.MapPost("/queryDocuments", async (QueryRequest reqBody, SapConnector sap, SqliteProvider db) =>
{
    if (reqBody == null || reqBody.DocumentIds == null || reqBody.DocumentIds.Length == 0) return Results.BadRequest("Provide document ids array");
    var docs = new List<DocumentPayload>();
    foreach (var id in reqBody.DocumentIds)
    {
        var doc = await db.GetDocumentCachedAsync(id);
        if (doc != null) { docs.Add(doc); continue; }
        var fetched = await sap.FetchDocumentByInternalIdAsync(id);
        if (fetched != null)
        {
            await db.UpsertDocumentAsync(fetched);
            docs.Add(fetched);
        }
    }
    return Results.Ok(docs);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", now = DateTime.UtcNow }));

app.Run();

class AppConfig
{
    public string SapDbServer => Environment.GetEnvironmentVariable("SAP_DB_SERVER") ?? "SAP_SERVER";
    public string SapDbName => Environment.GetEnvironmentVariable("SAP_DB_NAME") ?? "SBODEMOUS";
    public string SapUser => Environment.GetEnvironmentVariable("SAP_USER") ?? "manager";
    public string SapPass => Environment.GetEnvironmentVariable("SAP_PASS") ?? "manager";
    public string SapDbUser => Environment.GetEnvironmentVariable("SAP_DB_USER") ?? "sa";
    public string SapDbPass => Environment.GetEnvironmentVariable("SAP_DB_PASS") ?? "sqlpass";
    public string SapCompanyDb => Environment.GetEnvironmentVariable("SAP_COMPANYDB") ?? "SBODEMOUS";
    public string QuePagarBase => Environment.GetEnvironmentVariable("QUEPAGAR_BASE") ?? "https://quepagar.com/api/v1";
    public string QuePagarApiKey => Environment.GetEnvironmentVariable("QUEPAGAR_APIKEY") ?? "ABC";
    public int CronMinutes => int.TryParse(Environment.GetEnvironmentVariable("CRON_MINUTES"), out var m) ? m : 15;
    public string SqliteFile => Environment.GetEnvironmentVariable("SQLITE_FILE") ?? "appdata.db";
}

class SqliteProvider
{
    readonly string _cs;
    public SqliteProvider(AppConfig cfg)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = cfg.SqliteFile }.ToString();
        EnsureSchema();
        EnsureDefaultUser();
    }
    void EnsureSchema()
    {
        using var c = new SqliteConnection(_cs);
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS suppliers(CardCode TEXT PRIMARY KEY, CardName TEXT);
CREATE TABLE IF NOT EXISTS documents(InternalId TEXT PRIMARY KEY, Payload TEXT, DtUpdated TEXT);
CREATE TABLE IF NOT EXISTS sent_log(InternalId TEXT PRIMARY KEY, LastSent TEXT);
CREATE TABLE IF NOT EXISTS users(username TEXT PRIMARY KEY, password TEXT);
CREATE TABLE IF NOT EXISTS tokens(token TEXT PRIMARY KEY, username TEXT, expires TEXT);
";
        cmd.ExecuteNonQuery();
    }
    void EnsureDefaultUser()
    {
        using var c = new SqliteConnection(_cs);
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO users(username,password) VALUES('apiuser','apipass')";
        cmd.ExecuteNonQuery();
    }
    public async Task<bool> AddSupplierAsync(string code, string name)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO suppliers(CardCode,CardName) VALUES($c,$n)";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$n", name ?? "");
        var r = await cmd.ExecuteNonQueryAsync();
        return r > 0;
    }
    public async Task<bool> RemoveSupplierAsync(string code)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM suppliers WHERE CardCode=$c";
        cmd.Parameters.AddWithValue("$c", code);
        var r = await cmd.ExecuteNonQueryAsync();
        return r > 0;
    }
    public async Task<List<SupplierDto>> GetSuppliersAsync()
    {
        var list = new List<SupplierDto>();
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CardCode,CardName FROM suppliers";
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SupplierDto { CardCode = r.GetString(0), CardName = r.GetString(1) });
        }
        return list;
    }
    public async Task StoreSentAsync(string internalId, DateTime when)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO sent_log(InternalId,LastSent) VALUES($id,$dt)";
        cmd.Parameters.AddWithValue("$id", internalId);
        cmd.Parameters.AddWithValue("$dt", when.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<DateTime?> GetLastSentAsync(string internalId)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT LastSent FROM sent_log WHERE InternalId=$id";
        cmd.Parameters.AddWithValue("$id", internalId);
        var s = await cmd.ExecuteScalarAsync() as string;
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        return null;
    }
    public async Task UpsertDocumentAsync(DocumentPayload doc)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO documents(InternalId,Payload,DtUpdated) VALUES($id,$p,$u)";
        cmd.Parameters.AddWithValue("$id", doc.numDoc);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(doc));
        cmd.Parameters.AddWithValue("$u", doc.dt_updated ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<DocumentPayload> GetDocumentCachedAsync(string internalId)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Payload FROM documents WHERE InternalId=$id";
        cmd.Parameters.AddWithValue("$id", internalId);
        var s = await cmd.ExecuteScalarAsync() as string;
        if (string.IsNullOrEmpty(s)) return null;
        return JsonSerializer.Deserialize<DocumentPayload>(s);
    }
    public async Task<bool> ValidateUserAsync(string user, string pass)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM users WHERE username=$u AND password=$p";
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$p", pass);
        var cnt = (long)await cmd.ExecuteScalarAsync();
        return cnt > 0;
    }
    public async Task StoreIssuedTokenAsync(string token, string user, DateTime expires)
    {
        using var c = new SqliteConnection(_cs);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tokens(token,username,expires) VALUES($t,$u,$e)";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$e", expires.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }
}

record SupplierDto
{
    public string CardCode { get; init; }
    public string CardName { get; init; }
}

record QueryRequest
{
    public string[] DocumentIds { get; init; }
    public string Token { get; init; }
}

class ExternalApiClient
{
    readonly AppConfig _cfg;
    readonly HttpClient _http;
    public ExternalApiClient(AppConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(_cfg.QuePagarBase) };
    }
    public async Task<string> GetRemoteTokenAsync()
    {
        var q = $"token?apikey={WebUtility.UrlEncode(_cfg.QuePagarApiKey)}";
        var r = await _http.GetAsync(q);
        if (!r.IsSuccessStatusCode) return null;
        var s = await r.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("accessToken", out var t2)) return t2.GetString();
            return s.Trim('"');
        }
        catch
        {
            return s;
        }
    }
    public async Task<bool> PostDocumentsAsync(DocumentPayload[] docs)
    {
        var token = await GetRemoteTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;
        var payload = JsonSerializer.Serialize(docs);
        var req = new HttpRequestMessage(HttpMethod.Post, $"document?apikey={WebUtility.UrlEncode(_cfg.QuePagarApiKey)}");
        req.Headers.Add("X-Remote-Token", token);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var r = await _http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return false;
        var s = await r.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("success", out var succ)) return succ.GetBoolean();
        }
        catch { }
        return false;
    }
}

class SapConnector : IDisposable
{
    readonly AppConfig _cfg;
    Company _company;
    public SapConnector(AppConfig cfg)
    {
        _cfg = cfg;
    }
    public bool Connect()
    {
        if (_company != null && _company.Connected) return true;
        _company = new Company();
        _company.Server = _cfg.SapDbServer;
        _company.CompanyDB = _cfg.SapCompanyDb;
        _company.DbServerType = BoDataServerTypes.dst_MSSQL2017;
        _company.DbUserName = _cfg.SapDbUser;
        _company.DbPassword = _cfg.SapDbPass;
        _company.UserName = _cfg.SapUser;
        _company.Password = _cfg.SapPass;
        _company.language = BoSuppLangs.ln_English;
        var ret = _company.Connect();
        return ret == 0 && _company.Connected;
    }
    public void Dispose()
    {
        try { if (_company != null && _company.Connected) _company.Disconnect(); }
        catch { }
    }
    public async Task<List<DocumentPayload>> FetchUnpaidDocumentsForSuppliersAsync(List<string> supplierCardCodes, DateTime? onlyUpdatedSince)
    {
        if (supplierCardCodes == null || supplierCardCodes.Count == 0) return new List<DocumentPayload>();
        if (!Connect()) throw new Exception("SAP connection failed");
        var list = new List<DocumentPayload>();
        var codes = string.Join("','", supplierCardCodes.Select(s => s.Replace("'", "''")));
        var sinceClause = onlyUpdatedSince.HasValue ? $"AND (T0.UpdateDate > '{onlyUpdatedSince.Value:yyyyMMdd}' OR (T0.UpdateDate = '{onlyUpdatedSince.Value:yyyyMMdd}' AND T0.UpdateTime > '{onlyUpdatedSince.Value:HHmmss}'))" : "";
        var sqlInv = $"SELECT T0.DocEntry,T0.DocNum,T0.DocType, T0.DocDate, T0.DocDueDate, T0.CardCode, T0.CardName, T0.DocTotal, T0.VatSum, T0.Comments, T0.UpdateDate, T0.UpdateTime, T0.DocStatus FROM OINV T0 WHERE T0.CardCode IN ('{codes}') AND T0.DocStatus = 'O' {sinceClause}";
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery(sqlInv);
        while (!rs.EoF)
        {
            var doc = MapRecordToDocument(rs, "INVOICE");
            list.Add(doc);
            rs.MoveNext();
        }
        var sqlCredit = $"SELECT T0.DocEntry,T0.DocNum,T0.DocType, T0.DocDate, T0.DocDueDate, T0.CardCode, T0.CardName, T0.DocTotal, T0.VatSum, T0.Comments, T0.UpdateDate, T0.UpdateTime, T0.DocStatus FROM ORIN T0 WHERE T0.CardCode IN ('{codes}') AND T0.DocStatus = 'O' {sinceClause}";
        rs.DoQuery(sqlCredit);
        while (!rs.EoF)
        {
            var doc = MapRecordToDocument(rs, "CREDITNOTE");
            list.Add(doc);
            rs.MoveNext();
        }
        return list;
    }
    DocumentPayload MapRecordToDocument(Recordset rs, string type)
    {
        var docEntry = rs.Fields.Item("DocEntry").Value.ToString();
        var docNum = rs.Fields.Item("DocNum").Value.ToString();
        var docDate = DateTime.Parse(rs.Fields.Item("DocDate").Value.ToString());
        var issueDate = docDate.ToString("yyyy-MM-dd");
        var acctDate = docDate.ToString("yyyy-MM-dd");
        var cardCode = rs.Fields.Item("CardCode").Value.ToString();
        var cardName = rs.Fields.Item("CardName").Value.ToString();
        var comments = rs.Fields.Item("Comments")?.Value?.ToString() ?? "";
        var docTotal = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value);
        var vat = Convert.ToDecimal(rs.Fields.Item("VatSum").Value);
        var net = docTotal;
        var updateDate = rs.Fields.Item("UpdateDate")?.Value?.ToString();
        var updateTime = rs.Fields.Item("UpdateTime")?.Value?.ToString();
        string dtUpdated = null;
        if (!string.IsNullOrEmpty(updateDate))
        {
            var d = DateTime.ParseExact(updateDate, "yyyyMMdd", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(updateTime))
            {
                if (int.TryParse(updateTime, out var t))
                {
                    var s = updateTime.PadLeft(6, '0');
                    var hh = int.Parse(s.Substring(0, 2));
                    var mm = int.Parse(s.Substring(2, 2));
                    var ss = int.Parse(s.Substring(4, 2));
                    d = new DateTime(d.Year, d.Month, d.Day, hh, mm, ss);
                }
            }
            dtUpdated = d.ToString("yyyy-MM-dd HH:mm:ss");
        }
        var payload = new DocumentPayload
        {
            documentType = type == "INVOICE" ? "INVOICE" : "CREDITNOTE",
            serialNumber = cardCode + "-" + docNum,
            numDoc = docEntry,
            buyerID = "", issuerID = "", issuerName = cardName,
            comment = comments,
            issueDate = issueDate,
            accountingDate = acctDate,
            currency = "USD",
            amount = decimal.ToDouble(docTotal - vat),
            tax = decimal.ToDouble(vat),
            netIncome = decimal.ToDouble(docTotal),
            deductions = 0.0,
            discounts = 0.0,
            netOutcome = decimal.ToDouble(docTotal),
            payDateScheduled = null,
            payDateExecuted = null,
            dt_updated = dtUpdated,
            amountPaid = 0.0,
            status = "RECEIVED",
            beneficiaryID = null,
            creditNotesSerials = new string[0],
            compensations = new Compensation[0],
            freelancerId = Environment.GetEnvironmentVariable("FREELANCER_ID") ?? "unknown"
        };
        return payload;
    }
    public async Task<DocumentPayload> FetchDocumentByInternalIdAsync(string internalId)
    {
        if (!Connect()) return null;
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        var sql = $"SELECT T0.DocEntry,T0.DocNum,T0.DocDate, T0.CardCode, T0.CardName, T0.DocTotal, T0.VatSum, T0.Comments, T0.UpdateDate, T0.UpdateTime FROM OINV T0 WHERE T0.DocEntry = '{internalId}'";
        rs.DoQuery(sql);
        if (!rs.EoF) return MapRecordToDocument(rs, "INVOICE");
        sql = $"SELECT T0.DocEntry,T0.DocNum,T0.DocDate, T0.CardCode, T0.CardName, T0.DocTotal, T0.VatSum, T0.Comments, T0.UpdateDate, T0.UpdateTime FROM ORIN T0 WHERE T0.DocEntry = '{internalId}'";
        rs.DoQuery(sql);
        if (!rs.EoF) return MapRecordToDocument(rs, "CREDITNOTE");
        return null;
    }
}

class CronWorker : BackgroundService
{
    readonly IServiceProvider _sp;
    readonly AppConfig _cfg;
    public CronWorker(IServiceProvider sp, AppConfig cfg)
    {
        _sp = sp;
        _cfg = cfg;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SqliteProvider>();
                var sap = scope.ServiceProvider.GetRequiredService<SapConnector>();
                var api = scope.ServiceProvider.GetRequiredService<ExternalApiClient>();
                var suppliers = await db.GetSuppliersAsync();
                var codes = suppliers.Select(s => s.CardCode).ToList();
                DateTime? lastSince = null;
                var toSend = new List<DocumentPayload>();
                foreach (var code in codes)
                {
                    var lastSent = await db.GetLastSentAsync(code);
                    if (lastSent.HasValue)
                    {
                        if (!lastSince.HasValue || lastSent.Value < lastSince.Value) lastSince = lastSent.Value;
                    }
                }
                List<DocumentPayload> docs;
                if (codes.Count == 0) docs = new List<DocumentPayload>();
                else docs = await sap.FetchUnpaidDocumentsForSuppliersAsync(codes, lastSince);
                var groupedByInternal = docs.GroupBy(d => d.numDoc).Select(g => g.First()).ToArray();
                if (groupedByInternal.Length > 0)
                {
                    var ok = await api.PostDocumentsAsync(groupedByInternal);
                    if (ok)
                    {
                        foreach (var d in groupedByInternal)
                        {
                            await db.UpsertDocumentAsync(d);
                            await db.StoreSentAsync(d.numDoc, DateTime.UtcNow);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            await Task.Delay(TimeSpan.FromMinutes(_cfg.CronMinutes));
        }
    }
}

class DocumentPayload
{
    public string documentType { get; set; }
    public string serialNumber { get; set; }
    public string numDoc { get; set; }
    public string buyerID { get; set; }
    public string issuerID { get; set; }
    public string issuerName { get; set; }
    public string comment { get; set; }
    public string issueDate { get; set; }
    public string accountingDate { get; set; }
    public string currency { get; set; }
    public double amount { get; set; }
    public double tax { get; set; }
    public double netIncome { get; set; }
    public double deductions { get; set; }
    public double discounts { get; set; }
    public double netOutcome { get; set; }
    public string payDateScheduled { get; set; }
    public string payDateExecuted { get; set; }
    public string dt_updated { get; set; }
    public double amountPaid { get; set; }
    public string status { get; set; }
    public string beneficiaryID { get; set; }
    public string[] creditNotesSerials { get; set; }
    public Compensation[] compensations { get; set; }
    public string freelancerId { get; set; }
}

class Compensation
{
    public string dt { get; set; }
    public string currency { get; set; }
    public double amount { get; set; }
    public string type { get; set; }
    public string status { get; set; }
}
