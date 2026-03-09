using Microsoft.Data.Sqlite;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Use PORT env variable for cloud hosting (Render, Railway, etc.)
var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddCors();

var app = builder.Build();

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseDefaultFiles();
app.UseStaticFiles();

// SQLite database file — stored in the app directory
var dbPath = Path.Combine(app.Environment.ContentRootPath, "hydrastack.db");
var connStr = $"Data Source={dbPath}";

// Initialize database tables
using (var conn = new SqliteConnection(connStr))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Customers (
            CustomerID INTEGER PRIMARY KEY AUTOINCREMENT,
            FirstName TEXT NOT NULL,
            LastName TEXT NOT NULL,
            Email TEXT NOT NULL,
            Address TEXT DEFAULT '',
            City TEXT DEFAULT '',
            State TEXT DEFAULT '',
            ZipCode TEXT DEFAULT '',
            CreatedAt TEXT DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS Orders (
            OrderID INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderNumber TEXT NOT NULL UNIQUE,
            CustomerID INTEGER NOT NULL,
            OrderTotal REAL NOT NULL DEFAULT 0,
            PaymentReference TEXT,
            Status TEXT DEFAULT 'pending',
            CreatedAt TEXT DEFAULT (datetime('now')),
            FOREIGN KEY (CustomerID) REFERENCES Customers(CustomerID)
        );

        CREATE TABLE IF NOT EXISTS OrderItems (
            ItemID INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderID INTEGER NOT NULL,
            ProductName TEXT NOT NULL,
            LidType TEXT DEFAULT '',
            BaseType TEXT DEFAULT '',
            LidColor TEXT DEFAULT '',
            MidColor TEXT DEFAULT '',
            BaseColor TEXT DEFAULT '',
            Quantity INTEGER NOT NULL DEFAULT 1,
            Price REAL NOT NULL DEFAULT 0,
            FOREIGN KEY (OrderID) REFERENCES Orders(OrderID)
        );
    ";
    cmd.ExecuteNonQuery();
}

Console.WriteLine($"Database ready at: {dbPath}");

// POST /api/orders
app.MapPost("/api/orders", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var customer = body.GetProperty("customer");
    var items = body.GetProperty("items");
    var orderTotal = body.TryGetProperty("orderTotal", out var ot) ? ot.GetDouble() : 0.0;

    var firstName = customer.GetProperty("firstName").GetString() ?? "";
    var lastName = customer.GetProperty("lastName").GetString() ?? "";
    var email = customer.GetProperty("email").GetString() ?? "";
    var address = customer.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
    var city = customer.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
    var state = customer.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
    var zip = customer.TryGetProperty("zip", out var z) ? z.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "First name, last name, and email are required" });
        return;
    }

    try
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        // Insert customer
        long customerId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Customers (FirstName, LastName, Email, Address, City, State, ZipCode)
                VALUES ($fn, $ln, $em, $addr, $city, $state, $zip);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$fn", firstName);
            cmd.Parameters.AddWithValue("$ln", lastName);
            cmd.Parameters.AddWithValue("$em", email);
            cmd.Parameters.AddWithValue("$addr", address);
            cmd.Parameters.AddWithValue("$city", city);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$zip", zip);
            customerId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        var orderNumber = "HS-" + Random.Shared.Next(100000, 999999);

        // Insert order
        long orderId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Orders (OrderNumber, CustomerID, OrderTotal, Status)
                VALUES ($on, $cid, $total, 'pending');
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$on", orderNumber);
            cmd.Parameters.AddWithValue("$cid", customerId);
            cmd.Parameters.AddWithValue("$total", orderTotal);
            orderId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        // Insert items
        for (int i = 0; i < items.GetArrayLength(); i++)
        {
            var item = items[i];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO OrderItems (OrderID, ProductName, LidType, BaseType, LidColor, MidColor, BaseColor, Quantity, Price)
                VALUES ($oid, $name, $lid, $base, $lc, $mc, $bc, $qty, $price)";
            cmd.Parameters.AddWithValue("$oid", orderId);
            cmd.Parameters.AddWithValue("$name", item.TryGetProperty("name", out var n) ? n.GetString() ?? "HydraStack" : "HydraStack");
            cmd.Parameters.AddWithValue("$lid", item.TryGetProperty("lidType", out var lt) ? lt.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("$base", item.TryGetProperty("baseType", out var bt) ? bt.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("$lc", item.TryGetProperty("lidColor", out var lc2) ? lc2.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("$mc", item.TryGetProperty("midColor", out var mc) ? mc.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("$bc", item.TryGetProperty("baseColor", out var bc) ? bc.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("$qty", item.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1);
            cmd.Parameters.AddWithValue("$price", item.TryGetProperty("price", out var p) ? p.GetDouble() : 0.0);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        await ctx.Response.WriteAsJsonAsync(new { success = true, orderNumber, message = "Order placed successfully!" });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Order error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to place order. Please try again." });
    }
});

// GET /api/orders
app.MapGet("/api/orders", async () =>
{
    var orders = new List<object>();
    using var conn = new SqliteConnection(connStr);
    await conn.OpenAsync();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT o.OrderNumber, o.OrderTotal, o.Status, o.CreatedAt,
               c.FirstName, c.LastName, c.Email, c.City, c.State
        FROM Orders o
        JOIN Customers c ON o.CustomerID = c.CustomerID
        ORDER BY o.CreatedAt DESC";

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        orders.Add(new
        {
            OrderNumber = reader.GetString(0),
            OrderTotal = reader.GetDouble(1),
            Status = reader.GetString(2),
            CreatedAt = reader.GetString(3),
            FirstName = reader.GetString(4),
            LastName = reader.GetString(5),
            Email = reader.GetString(6),
            City = reader.GetString(7),
            State = reader.GetString(8),
        });
    }

    return Results.Json(orders);
});

app.Run();
